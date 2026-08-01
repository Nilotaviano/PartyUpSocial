let proof: string | null = null

export function setReauthProof(value: string) {
  proof = value
}

export function consumeReauthProof() {
  const current = proof
  proof = null
  return current
}
