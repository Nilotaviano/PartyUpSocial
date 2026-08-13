import { Bullets, P, Section } from './elements'
import { CONTACT_EMAIL } from '../legalLocale'

export function PrivacyEn() {
  return (
    <>
      <Section title="Who is responsible for your data">
        <P>
          PartyUp (partyup.social) is run by its owner as a natural person, who
          is the controller of the personal data described here. You can reach
          the controller at {CONTACT_EMAIL} for any question or request about
          this policy or your data.
        </P>
      </Section>

      <Section title="What we collect">
        <Bullets
          items={[
            'Account data: your username, display name, avatar choice, language and time zone.',
            'Your email address, when you sign up with one or add one later. We use it to sign you in, to confirm changes to your account, and to send the notifications you have turned on.',
            'Your Steam identity, only if you choose to link Steam: your Steam ID, your Steam persona name, and your Steam profile picture, which we copy so your avatar keeps working inside PartyUp.',
            'Gaming handles you enter for the platforms you play on.',
            'Availability data: the weekly blocks and one-off changes you mark as free or busy.',
            'Sessions and polls you create, join, decline, or vote in, together with their titles, notes, games and times.',
            'In-app notifications generated for you, and your notification preferences, including push subscriptions if you enable push.',
            'When you last used PartyUp, to show activity to administrators and measure active use.',
            'Guest responses: if you answer through a guest link, we store the name you give and your response, without an account.',
            'Technical data our servers see when you connect: your IP address, used for rate limiting and abuse protection, and short-lived technical logs of requests. We do not use it to profile you.',
          ]}
        />
      </Section>

      <Section title="How we use it and how analytics work">
        <P>
          We use the data above to run the product: to let you schedule
          sessions with friends, show who is free, deliver notifications, keep
          your account secure, and answer support requests.
        </P>
        <P>
          Our analytics are derived measures computed over the product data we
          already hold — how many accounts registered, how many people were
          active in a window, how many sessions and polls were created, how
          often a proposal turns into a confirmed session, and how often an
          organizer schedules again. There is no separate tracking pipeline: no
          advertising trackers, no third-party analytics service, no session
          replay, and no advertising or cross-site profiling of any kind. The
          &quot;active users&quot; measure and an administrator&apos;s view of
          your last activity both read the same &quot;when you last used
          PartyUp&quot; timestamp described above.
        </P>
      </Section>

      <Section title="Who we share it with">
        <Bullets
          items={[
            'Steam Web API (Valve): if you link Steam, we ask Steam for your public profile (Steam ID, persona name, avatar) and, when you run a friend import, your Steam friend list, which we use only to find matches during that import and do not store. Unlinking Steam revokes that access, deletes your stored Steam identity, and removes the Steam-sourced avatar copy from your profile.',
            'Our email delivery provider, which sends the sign-in, account and notification emails you receive.',
            'Other people inside PartyUp, according to the visibility rules of the product: friends, participants of a session or poll you take part in, and anyone holding a guest link you shared.',
            'Authorities, if we are legally required to disclose something.',
          ]}
        />
        <P>
          We do not sell your personal data and we do not share it for
          advertising.
        </P>
      </Section>

      <Section title="How long we keep it">
        <Bullets
          items={[
            'Your account data stays while your account exists.',
            'Content you shared into sessions and polls stays until your account is deleted, at which point the deletion effects below apply.',
            'Database backups are kept for 30 days and then rotated out. Deleted data can survive in a backup for up to that long before it ages out.',
          ]}
        />
      </Section>

      <Section title="Deleting your account">
        <Bullets
          items={[
            'You can delete your account from inside the app, and also from the public request page at /account-deletion if you cannot sign in.',
            'Deleting starts a seven-day grace period during which you can cancel the request.',
            'Starting a deletion signs you out on every device and invalidates your sessions.',
            'Signing in successfully during the grace period automatically cancels the deletion and restores normal use of your account.',
            'After the grace period we remove your friendships and your future-session participation, cancel the future sessions you own, and expire the polls you own. In completed-session history you appear as "Anonymous User" so the other participants keep an accurate record of what happened.',
          ]}
        />
      </Section>

      <Section title="Your rights">
        <P>
          Under Brazil&apos;s LGPD and comparable laws, you may confirm whether
          we process your data and access it, correct incomplete or inaccurate
          data, request deletion, request portability of your data, ask about
          who we share it with, and revoke a consent you gave — including by
          unlinking Steam or turning off notifications. Write to{' '}
          {CONTACT_EMAIL} to exercise any of these rights.
        </P>
      </Section>

      <Section title="Children">
        <P>
          PartyUp is for people aged 13 and over and is not directed to
          children under 13. We ask you to confirm you are at least 13 when you
          register; we store only whether that check passed and where the
          answer came from, never a birth date.
        </P>
      </Section>

      <Section title="Changes to this policy">
        <P>
          If we change how we handle your data, we update this page and its
          &quot;last updated&quot; date. Questions and requests go to{' '}
          {CONTACT_EMAIL}.
        </P>
      </Section>
    </>
  )
}

export function PrivacyPtBR() {
  return (
    <>
      <Section title="Quem é responsável pelos seus dados">
        <P>
          O PartyUp (partyup.social) é mantido pelo seu proprietário, pessoa
          natural, que é o controlador dos dados pessoais descritos aqui. Você
          pode falar com o controlador em {CONTACT_EMAIL} para qualquer dúvida
          ou pedido sobre esta política ou sobre os seus dados.
        </P>
      </Section>

      <Section title="O que coletamos">
        <Bullets
          items={[
            'Dados da conta: nome de usuário, nome de exibição, avatar escolhido, idioma e fuso horário.',
            'Seu e-mail, quando você se cadastra com um ou adiciona depois. Usamos para autenticar você, confirmar alterações na conta e enviar as notificações que você ativou.',
            'Sua identidade Steam, somente se você optar por vincular a Steam: seu Steam ID, seu nome de perfil (persona) e sua foto de perfil da Steam, que copiamos para que seu avatar continue funcionando dentro do PartyUp.',
            'Identificadores de jogo que você informa para as plataformas em que joga.',
            'Dados de disponibilidade: os blocos semanais e as exceções pontuais que você marca como livre ou ocupado.',
            'Sessões e enquetes que você cria, aceita, recusa ou vota, com seus títulos, observações, jogos e horários.',
            'Notificações geradas para você no aplicativo e suas preferências de notificação, incluindo assinaturas de push se você ativar push.',
            'Quando você usou o PartyUp pela última vez, para mostrar atividade aos administradores e medir o uso ativo.',
            'Respostas de convidados: se você responde por um link de convidado, guardamos o nome informado e a sua resposta, sem criar conta.',
            'Dados técnicos que nossos servidores recebem quando você se conecta: seu endereço IP, usado para limitação de requisições e proteção contra abuso, e registros técnicos de curta duração. Não os usamos para criar perfis sobre você.',
          ]}
        />
      </Section>

      <Section title="Como usamos e como funcionam as métricas">
        <P>
          Usamos os dados acima para operar o produto: permitir que você agende
          sessões com amigos, mostrar quem está livre, entregar notificações,
          manter sua conta segura e responder a pedidos de suporte.
        </P>
        <P>
          Nossas métricas são medidas derivadas, calculadas sobre os dados de
          produto que já temos — quantas contas se registraram, quantas pessoas
          estiveram ativas em um período, quantas sessões e enquetes foram
          criadas, com que frequência uma proposta vira sessão confirmada e com
          que frequência quem organiza agenda de novo. Não existe um pipeline
          de rastreamento separado: nenhum rastreador de publicidade, nenhum
          serviço de analytics de terceiros, nenhuma gravação de sessão e
          nenhum perfilamento publicitário ou entre sites. A medida de
          &quot;pessoas ativas&quot; e a visão de última atividade disponível
          para administradores leem o mesmo registro de &quot;quando você usou
          o PartyUp pela última vez&quot; descrito acima.
        </P>
      </Section>

      <Section title="Com quem compartilhamos">
        <Bullets
          items={[
            'Steam Web API (Valve): se você vincular a Steam, pedimos à Steam o seu perfil público (Steam ID, nome de perfil, avatar) e, quando você executa a importação de amigos, a sua lista de amigos da Steam, usada apenas para encontrar correspondências durante aquela importação e não armazenada. Desvincular a Steam revoga esse acesso, apaga a identidade Steam guardada e remove do seu perfil a cópia do avatar vinda da Steam.',
            'Nosso provedor de envio de e-mail, que entrega os e-mails de acesso, de conta e de notificação que você recebe.',
            'Outras pessoas dentro do PartyUp, conforme as regras de visibilidade do produto: amizades, participantes de uma sessão ou enquete de que você participa e quem tiver um link de convidado que você compartilhou.',
            'Autoridades, quando formos legalmente obrigados a divulgar algo.',
          ]}
        />
        <P>
          Não vendemos os seus dados pessoais e não os compartilhamos para
          publicidade.
        </P>
      </Section>

      <Section title="Por quanto tempo guardamos">
        <Bullets
          items={[
            'Os dados da sua conta permanecem enquanto a conta existir.',
            'O conteúdo que você compartilhou em sessões e enquetes permanece até a exclusão da sua conta, quando passam a valer os efeitos descritos abaixo.',
            'Os backups do banco de dados são mantidos por 30 dias e depois rotacionados. Dados excluídos podem sobreviver em um backup por até esse prazo antes de expirarem.',
          ]}
        />
      </Section>

      <Section title="Excluindo a sua conta">
        <Bullets
          items={[
            'Você pode excluir a conta dentro do aplicativo e também pela página pública de pedido em /account-deletion, caso não consiga entrar.',
            'A exclusão inicia um período de sete dias de carência, durante o qual você pode cancelar o pedido.',
            'Iniciar a exclusão desconecta você de todos os dispositivos e invalida as suas sessões de login.',
            'Entrar com sucesso durante a carência cancela automaticamente a exclusão e restaura o uso normal da conta.',
            'Depois da carência, removemos suas amizades e sua participação em sessões futuras, cancelamos as sessões futuras que você criou e expiramos as enquetes que você criou. No histórico de sessões concluídas você aparece como "Usuário Anônimo", para que as outras pessoas mantenham um registro fiel do que aconteceu.',
          ]}
        />
      </Section>

      <Section title="Seus direitos">
        <P>
          Pela LGPD, você pode confirmar a existência de tratamento e acessar
          seus dados, corrigir dados incompletos ou desatualizados, pedir a
          exclusão, pedir a portabilidade, saber com quem compartilhamos e
          revogar um consentimento que tenha dado — inclusive desvinculando a
          Steam ou desligando as notificações. Escreva para {CONTACT_EMAIL}{' '}
          para exercer qualquer um desses direitos.
        </P>
      </Section>

      <Section title="Crianças">
        <P>
          O PartyUp é para pessoas de 13 anos ou mais e não é direcionado a
          crianças menores de 13 anos. Pedimos que você confirme ter pelo menos
          13 anos ao se cadastrar; guardamos apenas se essa verificação passou
          e de onde veio a resposta, nunca uma data de nascimento.
        </P>
      </Section>

      <Section title="Mudanças nesta política">
        <P>
          Se mudarmos a forma como tratamos seus dados, atualizamos esta página
          e a sua data de última atualização. Dúvidas e pedidos podem ser
          enviados para {CONTACT_EMAIL}.
        </P>
      </Section>
    </>
  )
}

export function PrivacyEs() {
  return (
    <>
      <Section title="Quién es responsable de tus datos">
        <P>
          PartyUp (partyup.social) lo mantiene su propietario, como persona
          física, que es el responsable del tratamiento de los datos personales
          descritos aquí. Puedes escribir al responsable a {CONTACT_EMAIL} por
          cualquier duda o solicitud sobre esta política o sobre tus datos.
        </P>
      </Section>

      <Section title="Qué recopilamos">
        <Bullets
          items={[
            'Datos de la cuenta: nombre de usuario, nombre visible, avatar elegido, idioma y zona horaria.',
            'Tu correo electrónico, cuando te registras con uno o lo agregas después. Lo usamos para iniciar tu sesión, confirmar cambios en la cuenta y enviarte las notificaciones que hayas activado.',
            'Tu identidad de Steam, solo si eliges vincular Steam: tu Steam ID, tu nombre de perfil (persona) y tu foto de perfil de Steam, que copiamos para que tu avatar siga funcionando dentro de PartyUp.',
            'Los identificadores de juego que ingresas para las plataformas en las que juegas.',
            'Datos de disponibilidad: los bloques semanales y los cambios puntuales que marcas como libre u ocupado.',
            'Sesiones y encuestas que creas, aceptas, rechazas o votas, junto con sus títulos, notas, juegos y horarios.',
            'Notificaciones generadas para ti dentro de la aplicación y tus preferencias de notificación, incluidas las suscripciones push si activas push.',
            'Cuándo usaste PartyUp por última vez, para mostrar actividad a la administración y medir el uso activo.',
            'Respuestas de invitados: si respondes mediante un enlace de invitado, guardamos el nombre que indicas y tu respuesta, sin crear una cuenta.',
            'Datos técnicos que nuestros servidores reciben cuando te conectas: tu dirección IP, usada para limitar solicitudes y proteger contra abusos, y registros técnicos de corta duración. No los usamos para elaborar perfiles sobre ti.',
          ]}
        />
      </Section>

      <Section title="Cómo lo usamos y cómo funcionan las métricas">
        <P>
          Usamos los datos anteriores para operar el producto: permitirte
          agendar sesiones con tus amistades, mostrar quién está libre,
          entregar notificaciones, mantener tu cuenta segura y responder
          solicitudes de soporte.
        </P>
        <P>
          Nuestras métricas son medidas derivadas, calculadas sobre los datos
          de producto que ya tenemos: cuántas cuentas se registraron, cuántas
          personas estuvieron activas en un período, cuántas sesiones y
          encuestas se crearon, con qué frecuencia una propuesta se convierte
          en sesión confirmada y con qué frecuencia quien organiza vuelve a
          agendar. No existe una tubería de seguimiento aparte: ningún rastreador
          publicitario, ningún servicio de analítica de terceros, ninguna
          grabación de sesión y ningún perfilado publicitario o entre sitios.
          La medida de &quot;personas activas&quot; y la vista de última
          actividad que ve la administración leen el mismo registro de
          &quot;cuándo usaste PartyUp por última vez&quot; descrito arriba.
        </P>
      </Section>

      <Section title="Con quién lo compartimos">
        <Bullets
          items={[
            'Steam Web API (Valve): si vinculas Steam, le pedimos a Steam tu perfil público (Steam ID, nombre de perfil, avatar) y, cuando ejecutas la importación de amistades, tu lista de amigos de Steam, que usamos solo para encontrar coincidencias durante esa importación y no almacenamos. Desvincular Steam revoca ese acceso, borra la identidad de Steam guardada y elimina de tu perfil la copia del avatar proveniente de Steam.',
            'Nuestro proveedor de envío de correo, que entrega los correos de acceso, de cuenta y de notificación que recibes.',
            'Otras personas dentro de PartyUp, según las reglas de visibilidad del producto: amistades, participantes de una sesión o encuesta en la que participas y quien tenga un enlace de invitado que compartiste.',
            'Autoridades, si estamos legalmente obligados a divulgar algo.',
          ]}
        />
        <P>
          No vendemos tus datos personales ni los compartimos con fines
          publicitarios.
        </P>
      </Section>

      <Section title="Cuánto tiempo los conservamos">
        <Bullets
          items={[
            'Los datos de tu cuenta permanecen mientras exista la cuenta.',
            'El contenido que compartiste en sesiones y encuestas permanece hasta que se elimine tu cuenta, momento en el que aplican los efectos descritos abajo.',
            'Las copias de seguridad de la base de datos se conservan 30 días y luego se rotan. Los datos eliminados pueden sobrevivir en una copia de seguridad hasta ese plazo antes de expirar.',
          ]}
        />
      </Section>

      <Section title="Eliminar tu cuenta">
        <Bullets
          items={[
            'Puedes eliminar tu cuenta dentro de la aplicación y también desde la página pública de solicitud en /account-deletion si no logras iniciar sesión.',
            'La eliminación inicia un período de gracia de siete días durante el cual puedes cancelar la solicitud.',
            'Iniciar la eliminación cierra tu sesión en todos los dispositivos e invalida tus sesiones de acceso.',
            'Iniciar sesión con éxito durante el período de gracia cancela automáticamente la eliminación y restaura el uso normal de tu cuenta.',
            'Pasado el período de gracia eliminamos tus amistades y tu participación en sesiones futuras, cancelamos las sesiones futuras que creaste y expiramos las encuestas que creaste. En el historial de sesiones completadas apareces como "Usuario Anónimo", para que las demás personas conserven un registro fiel de lo ocurrido.',
          ]}
        />
      </Section>

      <Section title="Tus derechos">
        <P>
          Conforme a la LGPD de Brasil y a leyes comparables, puedes confirmar
          si tratamos tus datos y acceder a ellos, corregir datos incompletos o
          inexactos, solicitar su eliminación, solicitar su portabilidad,
          preguntar con quién los compartimos y revocar un consentimiento que
          hayas dado, incluso desvinculando Steam o desactivando las
          notificaciones. Escribe a {CONTACT_EMAIL} para ejercer cualquiera de
          estos derechos.
        </P>
      </Section>

      <Section title="Menores">
        <P>
          PartyUp es para personas de 13 años en adelante y no está dirigido a
          menores de 13 años. Te pedimos confirmar que tienes al menos 13 años
          al registrarte; solo guardamos si esa comprobación se superó y de
          dónde vino la respuesta, nunca una fecha de nacimiento.
        </P>
      </Section>

      <Section title="Cambios en esta política">
        <P>
          Si cambiamos la forma en que tratamos tus datos, actualizamos esta
          página y su fecha de última actualización. Las dudas y solicitudes van
          a {CONTACT_EMAIL}.
        </P>
      </Section>
    </>
  )
}
