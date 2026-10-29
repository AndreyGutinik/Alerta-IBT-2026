# Product

<!-- impeccable:product-schema 1 -->

## Platform

web

## Users
- **Operador de luz/telão da Igreja Batista (IBT)**, sozinho na mesa durante o culto, em um PC com várias telas (LED 2560x768, monitor de operação, TV). Usa o app do PC no dia a dia e, no celular, o site.
- **Equipe de estacionamento e recepção**: manda avisos de carro (modelo, cor, placa, motivo) pelo site no celular ou pelo Telegram; precisa de rapidez e zero dúvida.
- **Dono do projeto (administrador)**: gerencia quem da equipe pode enviar, histórico e configurações.

## Product Purpose
IBT Controller (antes "Alerta-IBT") mostra avisos de estacionamento numa barra que rola no LED da igreja, invisível para OBS/NDI, e controla o som da mesa: volumes dos programas, saídas de áudio (WING), player de músicas com despertadores e comandos para o Holyrics. Sucesso: o operador resolve tudo sem pensar, no meio do culto, sem erro e sem ruído visual.

## Positioning
Feito para esta igreja: recebe avisos por três portas (Telegram, site PWA, painel do PC), corrige e padroniza o texto (Gemini grátis + corretor offline) e junta aviso, som e música na mesma ferramenta, tudo gratuito.

## Operating Context
- App do PC: C# .NET 10 + WPF, ícone na bandeja, janela principal com abas Início, Som, Configurações; painel rápido da bandeja; instalação e atualização automática pelo GitHub Releases (Velopack).
- Site: PWA React + Vite em web/, Firebase (Hosting, Realtime Database, Auth Google/e-mail), https://alertaibt.web.app, uso principal no celular.
- Ambiente de desenvolvimento do dono: um PC comum; ambiente real: LED 2560x768 e mesa de som Behringer WING.
- Tema escuro (verde #1DA35F como ação principal), idioma pt-BR.

## Capabilities and Constraints
- Custo zero: nenhuma API paga, Firebase no plano Spark, Gemini no plano grátis.
- A barra do LED nunca pode vazar para OBS/NDI.
- Nome visível do produto: **IBT Controller**. Identificador interno (AlertaIBT), pasta de dados (%AppData%\AlertaIBT) e URL do site (alertaibt.web.app) NÃO mudam, para a atualização automática e as configurações continuarem funcionando.
- Janela do app: abre em ~1100 px e precisa funcionar de 900 a 1600 px (2 colunas a partir de ~1000 px; abaixo, uma coluna).
- Termos do domínio: aviso, telão/LED, despertador (música automática em dia e hora), playlist (pasta de músicas), fade, WING, Holyrics, pregação.

## Brand Commitments
Marca da IBT em Logo_IBT/ (ícone creme sobre escuro). Voz curta, direta, em português do Brasil. O dono pede: menos texto, mais ícones com dica ao passar o mouse, blocos bem separados e cartões com fundo mais claro que a página.

## Evidence on Hand
Telas atuais renderizadas em .superpowers/sdd/2026-10-19-alerta-ibt-v8-pc/batch-19/png/. Não há depoimentos, métricas nem fotos de clientes; não inventar.

## Product Principles
1. Uso no meio do culto: o estado (tocando, pausado, alerta ligado) precisa ser lido de relance.
2. Menos texto: ícone e dica no lugar de frases; só o essencial escrito.
3. Cada coisa no seu bloco: agrupar por tarefa, com espaço e fundo que separem.
4. Nada some sem querer: ações destrutivas pedem confirmação; o que falha mostra o motivo.
5. Mesmo idioma visual no app e no site.

## Accessibility & Inclusion
Contraste legível em tela escura, alvos de clique de pelo menos 32 px no PC e 44 px no celular, foco visível por teclado, movimento sutil e curto.
