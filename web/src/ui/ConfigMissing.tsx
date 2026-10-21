import logo from '../../../assets/icons/logo.svg'

/** O site foi publicado sem as variáveis do Firebase: em vez de quebrar, diz o que falta. */
export function ConfigMissing({ missing }: { missing: string[] }) {
  return (
    <main className="splash">
      <img className="logo" src={logo} alt="" width={72} height={72} />
      <h1>Site ainda não configurado</h1>
      <p className="sub">Configuração do Firebase ausente:</p>
      <ul className="missing">
        {missing.map((name) => (
          <li key={name}>
            <code>{name}</code>
          </li>
        ))}
      </ul>
      <p className="sub">Quem cuida do site precisa preencher essas variáveis (veja docs/SITE-COMO-PUBLICAR.md).</p>
    </main>
  )
}
