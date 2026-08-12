import { Bullets, P, Section } from './elements'
import { CONTACT_EMAIL } from '../legalLocale'

export function CommunityStandardsEn() {
  return (
    <>
      <Section title="Where these standards apply">
        <P>
          PartyUp has no session comments, but plenty of what you write is seen
          by other people: usernames, display names, gaming handles, session
          titles and notes, free-text game names, poll options, and the name a
          guest gives when responding to a link. These standards apply to all
          of it, and to how you behave towards other people on the service.
        </P>
      </Section>

      <Section title="What is not allowed">
        <Bullets
          items={[
            'Harassment, threats, bullying, or targeting someone with unwanted contact.',
            'Hate speech or attacks based on race, ethnicity, national origin, religion, disability, sex, gender identity, or sexual orientation.',
            'Sexual content involving minors, in any form. This is reported to the authorities and the account is removed permanently.',
            'Sexually explicit or graphically violent content.',
            'Spam, scams, phishing, or unsolicited advertising, including through guest links.',
            'Impersonating another person, a company, or a PartyUp administrator.',
            'Promoting cheats, exploits, account selling, or other ways to break the rules of the games you schedule.',
            'Sharing someone else’s private information without their consent.',
            'Attacking the service itself: abusing invitations or guest links, evading a ban, or automating requests to overwhelm it.',
          ]}
        />
      </Section>

      <Section title="Filtering">
        <P>
          Usernames and display names pass through a profanity filter, and the
          text fields above have length and character limits. A filter is a
          floor, not a licence: something that gets past it can still break
          these standards.
        </P>
      </Section>

      <Section title="Blocking">
        <P>
          You can block another person at any time. Blocking removes the
          connection and any shared participation between you, and the other
          person is not notified. Blocking is yours to use whenever you want —
          you never need a reason or our approval.
        </P>
      </Section>

      <Section title="What happens when a rule is broken">
        <Bullets
          items={[
            'Warning: we contact the account about what needs to change.',
            'Ban: the account is signed out everywhere, can no longer sign in, and stops appearing in lookup and discovery. Its sessions, polls and history are left as they are.',
            'Content removal: for serious or repeated cases, a ban can be accompanied by removing the account’s content — cancelling its sessions that have not started, expiring its open polls, and dropping its future participation. This is permanent, and lifting the ban later brings none of it back.',
          ]}
        />
        <P>
          Which step applies depends on how serious the case is; the most
          serious ones go straight to a ban with content removal.
        </P>
      </Section>

      <Section title="Reporting something">
        <P>
          In-app reporting is not built yet. Until it is, contact us at{' '}
          {CONTACT_EMAIL} with what you saw and where — the username, the
          session or poll, and the time it happened. We review reports and act
          on them as quickly as we can.
        </P>
      </Section>
    </>
  )
}

export function CommunityStandardsPtBR() {
  return (
    <>
      <Section title="Onde estes padrões valem">
        <P>
          O PartyUp não tem comentários em sessões, mas muita coisa que você
          escreve é vista por outras pessoas: nomes de usuário, nomes de
          exibição, identificadores de jogo, títulos e observações de sessões,
          nomes de jogos digitados livremente, opções de enquete e o nome que
          um convidado informa ao responder um link. Estes padrões valem para
          tudo isso e para como você se comporta com as outras pessoas no
          serviço.
        </P>
      </Section>

      <Section title="O que não é permitido">
        <Bullets
          items={[
            'Assédio, ameaças, intimidação ou perseguir alguém com contato indesejado.',
            'Discurso de ódio ou ataques por raça, etnia, origem nacional, religião, deficiência, sexo, identidade de gênero ou orientação sexual.',
            'Conteúdo sexual envolvendo menores, em qualquer forma. Isso é comunicado às autoridades e a conta é removida em definitivo.',
            'Conteúdo sexualmente explícito ou de violência gráfica.',
            'Spam, golpes, phishing ou publicidade não solicitada, inclusive por links de convidado.',
            'Passar-se por outra pessoa, por uma empresa ou por uma administração do PartyUp.',
            'Promover cheats, exploits, venda de contas ou outras formas de burlar as regras dos jogos que você agenda.',
            'Divulgar informações privadas de outra pessoa sem o consentimento dela.',
            'Atacar o próprio serviço: abusar de convites ou links de convidado, burlar um veto ou automatizar requisições para sobrecarregá-lo.',
          ]}
        />
      </Section>

      <Section title="Filtragem">
        <P>
          Nomes de usuário e nomes de exibição passam por um filtro de
          palavrões, e os campos de texto acima têm limites de tamanho e de
          caracteres. O filtro é um piso, não uma permissão: algo que passa por
          ele ainda pode violar estes padrões.
        </P>
      </Section>

      <Section title="Bloqueio entre pessoas">
        <P>
          Você pode bloquear outra pessoa a qualquer momento. O bloqueio
          elimina a conexão e a participação compartilhada entre vocês, e a
          outra pessoa não é notificada. O bloqueio é seu para usar quando
          quiser — você nunca precisa de um motivo nem da nossa aprovação.
        </P>
      </Section>

      <Section title="O que acontece quando uma regra é violada">
        <Bullets
          items={[
            'Aviso: entramos em contato com a conta sobre o que precisa mudar.',
            'Veto: a conta é desconectada de todos os dispositivos, deixa de poder entrar e some da busca e da descoberta. Suas sessões, enquetes e histórico ficam como estão.',
            'Remoção de conteúdo: em casos graves ou repetidos, o veto pode vir acompanhado da remoção do conteúdo da conta — cancelando as sessões que ainda não começaram, expirando as enquetes abertas e removendo a participação futura. Isso é permanente, e retirar o veto depois não traz nada de volta.',
          ]}
        />
        <P>
          Qual passo se aplica depende da gravidade do caso; os mais graves vão
          direto para o veto com remoção de conteúdo.
        </P>
      </Section>

      <Section title="Como relatar algo">
        <P>
          A denúncia dentro do aplicativo ainda não existe. Até existir, fale
          com a gente em {CONTACT_EMAIL} contando o que você viu e onde — o
          nome de usuário, a sessão ou enquete e quando aconteceu. Analisamos
          os relatos e agimos o mais rápido que conseguimos.
        </P>
      </Section>
    </>
  )
}

export function CommunityStandardsEs() {
  return (
    <>
      <Section title="Dónde se aplican estas normas">
        <P>
          PartyUp no tiene comentarios en las sesiones, pero mucho de lo que
          escribes lo ven otras personas: nombres de usuario, nombres visibles,
          identificadores de juego, títulos y notas de sesiones, nombres de
          juegos escritos libremente, opciones de encuesta y el nombre que
          indica una persona invitada al responder un enlace. Estas normas se
          aplican a todo eso y a cómo tratas a las demás personas en el
          servicio.
        </P>
      </Section>

      <Section title="Qué no está permitido">
        <Bullets
          items={[
            'Acoso, amenazas, intimidación o buscar a alguien con contacto no deseado.',
            'Discurso de odio o ataques por raza, etnia, origen nacional, religión, discapacidad, sexo, identidad de género u orientación sexual.',
            'Contenido sexual que involucre a menores, en cualquier forma. Se informa a las autoridades y la cuenta se elimina de forma permanente.',
            'Contenido sexualmente explícito o de violencia gráfica.',
            'Spam, estafas, phishing o publicidad no solicitada, incluso por enlaces de invitado.',
            'Hacerse pasar por otra persona, por una empresa o por la administración de PartyUp.',
            'Promover trampas, exploits, venta de cuentas u otras formas de romper las reglas de los juegos que agendas.',
            'Difundir información privada de otra persona sin su consentimiento.',
            'Atacar el propio servicio: abusar de invitaciones o enlaces de invitado, evadir un veto o automatizar solicitudes para saturarlo.',
          ]}
        />
      </Section>

      <Section title="Filtrado">
        <P>
          Los nombres de usuario y los nombres visibles pasan por un filtro de
          groserías, y los campos de texto anteriores tienen límites de largo y
          de caracteres. El filtro es un piso, no un permiso: algo que lo
          atraviesa igual puede incumplir estas normas.
        </P>
      </Section>

      <Section title="Bloqueo entre personas">
        <P>
          Puedes bloquear a otra persona cuando quieras. El bloqueo elimina la
          conexión y la participación compartida entre ustedes, y la otra
          persona no recibe una notificación. El bloqueo es tuyo para usarlo
          cuando quieras: nunca necesitas un motivo ni nuestra aprobación.
        </P>
      </Section>

      <Section title="Qué pasa cuando se incumple una norma">
        <Bullets
          items={[
            'Aviso: contactamos a la cuenta sobre lo que debe cambiar.',
            'Veto: la cuenta se cierra en todos los dispositivos, deja de poder iniciar sesión y desaparece de la búsqueda y del descubrimiento. Sus sesiones, encuestas e historial quedan tal cual están.',
            'Eliminación de contenido: en casos graves o repetidos, el veto puede ir acompañado de la eliminación del contenido de la cuenta: cancelar sus sesiones que aún no empezaron, expirar sus encuestas abiertas y quitar su participación futura. Es permanente, y quitar el veto después no recupera nada.',
          ]}
        />
        <P>
          Qué paso se aplica depende de la gravedad del caso; los más graves van
          directo al veto con eliminación de contenido.
        </P>
      </Section>

      <Section title="Cómo informar algo">
        <P>
          Todavía no existe el reporte dentro de la aplicación. Hasta que
          exista, contáctanos a {CONTACT_EMAIL} con lo que viste y dónde: el
          nombre de usuario, la sesión o encuesta y cuándo ocurrió. Revisamos
          los reportes y actuamos lo más rápido que podemos.
        </P>
      </Section>
    </>
  )
}
