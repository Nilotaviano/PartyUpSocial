import { Bullets, P, Section } from './elements'
import { CONTACT_EMAIL } from '../legalLocale'

export function TermsEn() {
  return (
    <>
      <Section title="What PartyUp is">
        <P>
          PartyUp is a scheduling service for friends who play games together.
          You create sessions with a fixed time or polls with candidate times,
          invite friends or share a guest link, mark when you are free, and get
          notified when plans change. The service is provided as it is, by its
          owner as a natural person, and is free to use.
        </P>
      </Section>

      <Section title="Your account">
        <Bullets
          items={[
            'You are responsible for what happens under your account, including keeping your sign-in method and any guest links you share under your control.',
            'The information you put in your profile — username, display name, gaming handles — must be yours to use, and must not impersonate someone else.',
            'You may sign in with an email address, a magic link, or a linked Steam account. You can unlink Steam at any time as long as you keep at least one way to sign in.',
          ]}
        />
      </Section>

      <Section title="Who may use it">
        <P>
          PartyUp is for people aged 13 and over. It is not directed to
          children under 13, and you must confirm you meet the age requirement
          when you register. Guests answering through a link do not create an
          account.
        </P>
      </Section>

      <Section title="Acceptable use">
        <P>
          Session titles, notes, game names, usernames and display names are
          visible to other people. What is and is not allowed in them is set
          out in our Community Standards, which are part of these terms.
        </P>
      </Section>

      <Section title="Enforcement">
        <Bullets
          items={[
            'If an account breaks these terms or the Community Standards, we may ban it. A banned account is signed out everywhere, can no longer sign in, and stops appearing in lookup and discovery; its sessions, polls and history are left as they are.',
            'A ban may also be accompanied by removal of the account’s content, which cancels its sessions that have not started, expires its open polls, and drops its future participation.',
            'Content removal is permanent. Lifting a ban later restores access to the account, but it does not bring back anything that was removed.',
            'If you think a decision was wrong, write to ' + CONTACT_EMAIL + '.',
          ]}
        />
      </Section>

      <Section title="Deleting your account">
        <P>
          You can delete your account at any time from inside the app, or from
          the public request page at /account-deletion if you cannot sign in.
          Deletion starts a seven-day grace period, signs you out everywhere,
          and is cancelled automatically if you sign in successfully before the
          period ends. What happens to your data afterwards is described in the
          Privacy Policy.
        </P>
      </Section>

      <Section title="No warranty">
        <P>
          PartyUp is provided as is, without any warranty. We do not promise
          that it will always be available, that notifications and reminders
          will always arrive, or that the information other people put into it
          is accurate. Treat a scheduled session as a plan between people, not
          as a guarantee.
        </P>
      </Section>

      <Section title="Limitation of liability">
        <P>
          To the extent the law allows, the owner is not liable for indirect or
          incidental damages, for missed plans, or for anything that arises out
          of your use of PartyUp. Nothing here limits rights you have that
          cannot be limited by agreement, including consumer rights.
        </P>
      </Section>

      <Section title="Changes to these terms">
        <P>
          We may update these terms as the product changes. The current version
          always lives on this page with its own last-updated date; continuing
          to use PartyUp after an update means you accept it.
        </P>
      </Section>

      <Section title="Governing law">
        <P>
          [Pending owner review: governing law and venue are not settled yet.
          The intended default is the law of Brazil, the owner&apos;s
          jurisdiction, with consumer-law protections of your own country
          preserved where they apply.]
        </P>
      </Section>

      <Section title="Contact">
        <P>Questions about these terms go to {CONTACT_EMAIL}.</P>
      </Section>
    </>
  )
}

export function TermsPtBR() {
  return (
    <>
      <Section title="O que é o PartyUp">
        <P>
          O PartyUp é um serviço de agendamento para amigos que jogam juntos.
          Você cria sessões com horário fixo ou enquetes com horários
          candidatos, convida amigos ou compartilha um link de convidado, marca
          quando está livre e recebe aviso quando os planos mudam. O serviço é
          oferecido no estado em que se encontra, pelo seu proprietário, pessoa
          natural, e é gratuito.
        </P>
      </Section>

      <Section title="Sua conta">
        <Bullets
          items={[
            'Você é responsável pelo que acontece na sua conta, inclusive por manter sob seu controle o seu método de acesso e os links de convidado que compartilhar.',
            'As informações do seu perfil — nome de usuário, nome de exibição, identificadores de jogo — devem ser suas e não podem se passar por outra pessoa.',
            'Você pode entrar com e-mail, com link mágico ou com uma conta Steam vinculada. Pode desvincular a Steam a qualquer momento, desde que mantenha ao menos uma forma de entrar.',
          ]}
        />
      </Section>

      <Section title="Quem pode usar">
        <P>
          O PartyUp é para pessoas de 13 anos ou mais. Não é direcionado a
          crianças menores de 13 anos, e você precisa confirmar que atende ao
          requisito de idade ao se cadastrar. Convidados que respondem por link
          não criam conta.
        </P>
      </Section>

      <Section title="Uso aceitável">
        <P>
          Títulos de sessão, observações, nomes de jogos, nomes de usuário e
          nomes de exibição ficam visíveis para outras pessoas. O que é e o que
          não é permitido neles está nos nossos Padrões da Comunidade, que
          fazem parte destes termos.
        </P>
      </Section>

      <Section title="Aplicação das regras">
        <Bullets
          items={[
            'Se uma conta violar estes termos ou os Padrões da Comunidade, podemos vetá-la. Uma conta vetada é desconectada de todos os dispositivos, deixa de poder entrar e some da busca e da descoberta; suas sessões, enquetes e histórico ficam como estão.',
            'O veto também pode vir acompanhado da remoção do conteúdo da conta, que cancela as sessões que ainda não começaram, expira as enquetes abertas e remove a participação futura.',
            'A remoção de conteúdo é permanente. Retirar o veto depois devolve o acesso à conta, mas não traz de volta nada que tenha sido removido.',
            'Se você achar que uma decisão foi equivocada, escreva para ' + CONTACT_EMAIL + '.',
          ]}
        />
      </Section>

      <Section title="Excluindo a sua conta">
        <P>
          Você pode excluir a sua conta a qualquer momento dentro do aplicativo
          ou pela página pública de pedido em /account-deletion, caso não
          consiga entrar. A exclusão inicia um período de sete dias de
          carência, desconecta você de todos os dispositivos e é cancelada
          automaticamente se você entrar com sucesso antes do fim do prazo. O
          que acontece com os seus dados depois disso está na Política de
          Privacidade.
        </P>
      </Section>

      <Section title="Sem garantias">
        <P>
          O PartyUp é oferecido no estado em que se encontra, sem garantias.
          Não prometemos que ele estará sempre disponível, que notificações e
          lembretes sempre chegarão, nem que as informações colocadas por
          outras pessoas são corretas. Trate uma sessão agendada como um
          combinado entre pessoas, não como uma garantia.
        </P>
      </Section>

      <Section title="Limitação de responsabilidade">
        <P>
          Na medida em que a lei permitir, o proprietário não responde por
          danos indiretos ou incidentais, por planos perdidos, nem por qualquer
          coisa decorrente do seu uso do PartyUp. Nada aqui limita direitos que
          você tenha e que não possam ser limitados por acordo, inclusive
          direitos do consumidor.
        </P>
      </Section>

      <Section title="Mudanças nestes termos">
        <P>
          Podemos atualizar estes termos conforme o produto evolui. A versão
          vigente fica sempre nesta página, com a sua própria data de última
          atualização; continuar usando o PartyUp depois de uma atualização
          significa aceitá-la.
        </P>
      </Section>

      <Section title="Lei aplicável">
        <P>
          [Pendente de revisão do proprietário: a lei aplicável e o foro ainda
          não estão definidos. O padrão pretendido é a lei do Brasil, a
          jurisdição do proprietário, preservadas as proteções de direito do
          consumidor do seu país quando aplicáveis.]
        </P>
      </Section>

      <Section title="Contato">
        <P>Dúvidas sobre estes termos podem ser enviadas para {CONTACT_EMAIL}.</P>
      </Section>
    </>
  )
}

export function TermsEs() {
  return (
    <>
      <Section title="Qué es PartyUp">
        <P>
          PartyUp es un servicio de agenda para amistades que juegan juntas.
          Creas sesiones con horario fijo o encuestas con horarios candidatos,
          invitas a tus amistades o compartes un enlace de invitado, marcas
          cuándo estás libre y recibes aviso cuando los planes cambian. El
          servicio se ofrece tal cual está, por su propietario como persona
          física, y es gratuito.
        </P>
      </Section>

      <Section title="Tu cuenta">
        <Bullets
          items={[
            'Eres responsable de lo que ocurre en tu cuenta, incluido mantener bajo tu control tu método de acceso y los enlaces de invitado que compartas.',
            'La información de tu perfil — nombre de usuario, nombre visible, identificadores de juego — debe ser tuya y no puede suplantar a otra persona.',
            'Puedes iniciar sesión con correo, con enlace mágico o con una cuenta de Steam vinculada. Puedes desvincular Steam cuando quieras, siempre que conserves al menos una forma de iniciar sesión.',
          ]}
        />
      </Section>

      <Section title="Quién puede usarlo">
        <P>
          PartyUp es para personas de 13 años en adelante. No está dirigido a
          menores de 13 años y debes confirmar que cumples el requisito de edad
          al registrarte. Las personas invitadas que responden por enlace no
          crean cuenta.
        </P>
      </Section>

      <Section title="Uso aceptable">
        <P>
          Los títulos de sesión, las notas, los nombres de juegos, los nombres
          de usuario y los nombres visibles quedan a la vista de otras
          personas. Lo que está permitido y lo que no está en nuestras Normas
          de la Comunidad, que forman parte de estos términos.
        </P>
      </Section>

      <Section title="Aplicación de las normas">
        <Bullets
          items={[
            'Si una cuenta incumple estos términos o las Normas de la Comunidad, podemos vetarla. Una cuenta vetada se cierra en todos los dispositivos, deja de poder iniciar sesión y desaparece de la búsqueda y del descubrimiento; sus sesiones, encuestas e historial quedan tal cual están.',
            'El veto también puede ir acompañado de la eliminación del contenido de la cuenta, que cancela sus sesiones que aún no empezaron, expira sus encuestas abiertas y quita su participación futura.',
            'La eliminación de contenido es permanente. Quitar el veto después devuelve el acceso a la cuenta, pero no recupera nada de lo eliminado.',
            'Si crees que una decisión fue equivocada, escribe a ' + CONTACT_EMAIL + '.',
          ]}
        />
      </Section>

      <Section title="Eliminar tu cuenta">
        <P>
          Puedes eliminar tu cuenta cuando quieras desde la aplicación o desde
          la página pública de solicitud en /account-deletion si no logras
          iniciar sesión. La eliminación inicia un período de gracia de siete
          días, cierra tu sesión en todos los dispositivos y se cancela
          automáticamente si inicias sesión con éxito antes de que termine. Lo
          que ocurre después con tus datos está en la Política de Privacidad.
        </P>
      </Section>

      <Section title="Sin garantías">
        <P>
          PartyUp se ofrece tal cual está, sin garantía alguna. No prometemos
          que siempre esté disponible, que las notificaciones y los
          recordatorios siempre lleguen, ni que la información que ponen otras
          personas sea exacta. Trata una sesión agendada como un acuerdo entre
          personas, no como una garantía.
        </P>
      </Section>

      <Section title="Limitación de responsabilidad">
        <P>
          En la medida en que la ley lo permita, el propietario no responde por
          daños indirectos o incidentales, por planes perdidos ni por nada que
          derive de tu uso de PartyUp. Nada de lo anterior limita derechos que
          tengas y que no puedan limitarse por acuerdo, incluidos los derechos
          de consumidor.
        </P>
      </Section>

      <Section title="Cambios en estos términos">
        <P>
          Podemos actualizar estos términos a medida que el producto cambia. La
          versión vigente siempre está en esta página, con su propia fecha de
          última actualización; seguir usando PartyUp después de una
          actualización significa que la aceptas.
        </P>
      </Section>

      <Section title="Ley aplicable">
        <P>
          [Pendiente de revisión del propietario: la ley aplicable y el fuero
          todavía no están definidos. El valor previsto es la ley de Brasil, la
          jurisdicción del propietario, conservando las protecciones de derecho
          del consumidor de tu país cuando correspondan.]
        </P>
      </Section>

      <Section title="Contacto">
        <P>Las dudas sobre estos términos van a {CONTACT_EMAIL}.</P>
      </Section>
    </>
  )
}
