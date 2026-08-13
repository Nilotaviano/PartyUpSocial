#!/usr/bin/env bash
#
# Provision an Ubuntu 24.04 VM as an autonomous Claude Code workstation for
# PartyUpSocial: .NET 10 SDK, Node 20, native Docker (dev Postgres +
# Testcontainers + built browser topology), Playwright browsers, PowerShell,
# tmux, Claude Code CLI, repo clone, and a first full gate run.
#
# Usage (as a sudo-capable non-root user, e.g. `ubuntu`):
#   GIT_NAME="Nilo Silva" GIT_EMAIL="nilotaviano@gmail.com" \
#   REPO_URL="https://github.com/Nilotaviano/PartyUpSocial.git" \
#   bash provision-agent-vm.sh
#
# Auth you must supply interactively or beforehand:
#   - a GitHub credential for clone/push: either a PAT (script prompts via
#     git credential store) or an SSH deploy key you install yourself.
#   - commit signing: this script disables it for the VM clone by default
#     (flip SIGN_COMMITS=1 and install a VM-local signing key if you want
#     verified commits from the agent).
set -euo pipefail

GIT_NAME="${GIT_NAME:?set GIT_NAME}"
GIT_EMAIL="${GIT_EMAIL:?set GIT_EMAIL}"
REPO_URL="${REPO_URL:-https://github.com/Nilotaviano/PartyUpSocial.git}"
REPO_DIR="${REPO_DIR:-$HOME/PartyUpSocial}"
SIGN_COMMITS="${SIGN_COMMITS:-0}"
DOTNET_CHANNEL="${DOTNET_CHANNEL:-10.0}"
NODE_MAJOR="${NODE_MAJOR:-20}"

echo "== base packages =="
sudo apt-get update -y
sudo apt-get install -y \
  ca-certificates curl git jq tmux unzip zip build-essential \
  apt-transport-https gnupg lsb-release

echo "== .NET ${DOTNET_CHANNEL} SDK (apt) =="
SDK_PKG="dotnet-sdk-${DOTNET_CHANNEL}"
if ! sudo apt-get install -y "$SDK_PKG"; then
  # Ubuntu's feed lags new majors sometimes — fall back to Microsoft's repo.
  echo "-- ${SDK_PKG} not in Ubuntu's feed; adding packages.microsoft.com --"
  curl -fsSL "https://packages.microsoft.com/config/ubuntu/$(. /etc/os-release && echo "$VERSION_ID")/packages-microsoft-prod.deb" \
    -o /tmp/packages-microsoft-prod.deb
  sudo dpkg -i /tmp/packages-microsoft-prod.deb
  sudo apt-get update -y
  sudo apt-get install -y "$SDK_PKG"
fi
# dotnet global tools (pwsh below) install to ~/.dotnet/tools regardless of SDK origin.
if ! grep -q '.dotnet/tools' "$HOME/.bashrc"; then
  echo 'export PATH="$HOME/.dotnet/tools:$PATH"' >> "$HOME/.bashrc"
fi
export PATH="$HOME/.dotnet/tools:$PATH"
dotnet --version

echo "== Node ${NODE_MAJOR} (NodeSource) =="
curl -fsSL "https://deb.nodesource.com/setup_${NODE_MAJOR}.x" | sudo -E bash -
sudo apt-get install -y nodejs
node --version && npm --version

echo "== Docker Engine (native; Testcontainers + dev Postgres + built browser topology) =="
if ! command -v docker >/dev/null; then
  sudo install -m 0755 -d /etc/apt/keyrings
  curl -fsSL https://download.docker.com/linux/ubuntu/gpg | \
    sudo gpg --dearmor -o /etc/apt/keyrings/docker.gpg
  echo "deb [arch=$(dpkg --print-architecture) signed-by=/etc/apt/keyrings/docker.gpg] \
https://download.docker.com/linux/ubuntu $(. /etc/os-release && echo "$VERSION_CODENAME") stable" | \
    sudo tee /etc/apt/sources.list.d/docker.list > /dev/null
  sudo apt-get update -y
  sudo apt-get install -y docker-ce docker-ce-cli containerd.io docker-compose-plugin
fi
sudo usermod -aG docker "$USER"
sudo systemctl enable --now docker

echo "== PowerShell (Playwright's install script host) =="
if ! command -v pwsh >/dev/null; then
  dotnet tool install -g powershell || dotnet tool update -g powershell
fi

echo "== git identity =="
git config --global user.name  "$GIT_NAME"
git config --global user.email "$GIT_EMAIL"
git config --global init.defaultBranch main
if [ "$SIGN_COMMITS" = "0" ]; then
  git config --global commit.gpgsign false
fi
# PAT flow: cache credentials on first push. Swap for an SSH key if preferred.
git config --global credential.helper store

echo "== clone repo =="
if [ ! -d "$REPO_DIR/.git" ]; then
  git clone "$REPO_URL" "$REPO_DIR"
fi
cd "$REPO_DIR"

echo "== restore + build backend =="
dotnet tool restore
dotnet build PartyUp.sln

echo "== web dependencies =="
(cd web && npm ci)

echo "== Playwright browsers + system deps =="
pwsh tests/PartyUp.BrowserTests/bin/Debug/net10.0/playwright.ps1 \
  install --with-deps chromium firefox webkit

echo "== dev Postgres =="
# Group membership needs a re-login; use sg for this first run.
sg docker -c "docker compose -f deploy/compose.dev.yml up -d"

echo "== Claude Code CLI =="
if ! command -v claude >/dev/null; then
  curl -fsSL https://claude.ai/install.sh | bash
fi
if ! grep -q '.local/bin' "$HOME/.bashrc"; then
  echo 'export PATH="$HOME/.local/bin:$PATH"' >> "$HOME/.bashrc"
fi

echo "== smoke: unit tests (no Docker needed) =="
dotnet test PartyUp.sln --filter "Category!=Integration" -v q

cat <<'EOF'

== provisioning done — manual steps remaining ==
1. Re-login (or `newgrp docker`) so your user picks up the docker group.
2. `claude` once interactively to log in (Anthropic account).
3. First `git push` will prompt for the GitHub PAT (stored via credential store),
   or install an SSH deploy key and switch the remote to git@.
4. If you want signed commits from the VM: create a VM-local SSH signing key,
   add it to GitHub as a *signing* key, then:
     git config --global gpg.format ssh
     git config --global user.signingkey ~/.ssh/id_ed25519_signing.pub
     git config --global commit.gpgsign true
5. Full gates, exactly as on the Windows host but without the WSL wrapper:
     bash scripts/wsl-gate.sh                    # backend shards
     (cd web && npm run lint && npm run build && npm test)
     dotnet test tests/PartyUp.BrowserTests -p:PartyUpBrowserTests=true \
       --filter "Category=Integration&Browser!=WebKit"
6. Run Claude Code inside tmux so SSH drops don't kill the session:
     tmux new -s claude
     claude
   Hand it .foreman/ledger.md to resume the current work state.
EOF
