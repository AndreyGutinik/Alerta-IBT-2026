# Política de privacidade do IBT Controller

O IBT Controller mostra avisos curtos de estacionamento no LED da igreja. Esta página diz, sem rodeios, que dados o app e o site tratam e para onde eles vão.

## O que é tratado

- **O texto dos avisos** (carro, cor, placa e motivo, ou um aviso livre) e **quem o enviou** (nome e e-mail da conta Google no site; nome e identificador do Telegram no grupo).
- **Configurações do PC** (token do bot, chave do Gemini, dados da conta do PC no Firebase), guardadas só no computador da igreja, em `%AppData%\AlertaIBT`, com os segredos **criptografados** pelo Windows (DPAPI, só o mesmo usuário do Windows lê).
- **O teclado, só para um gesto**: com "Ctrl 3 vezes traz o mouse ao centro" ligado (é o padrão), o app usa um gancho de teclado do Windows apenas para contar três toques seguidos na tecla Ctrl e levar o mouse ao centro da tela principal. Ele não grava, não guarda e não envia nenhuma tecla (nem no log). O travamento do mouse só prende o cursor ao monitor principal.
- **Registros (logs)** do app, só no computador, apagados depois de 7 dias. Eles não guardam senhas, chaves nem tokens.

## Para onde os dados vão

| Serviço | O que recebe | Para quê |
|---|---|---|
| **Telegram** | As mensagens do grupo e as respostas do bot | Receber avisos e responder a quem enviou |
| **Google Gemini** (plano grátis) | O texto do aviso (carro, cor, placa, motivo) | Corrigir ortografia e organizar o aviso. **No plano grátis o Google pode usar os textos para melhorar os produtos dele.** Dá para desligar em Início → Correção (ícone «Só offline»); o corretor offline continua funcionando |
| **Firebase / Google Cloud** (`alertaibt`) | Conta Google (nome, e-mail), os avisos enviados pelo site e o estado do telão | Entrar no site, enviar e acompanhar avisos. Pedidos finalizados são apagados do banco em até 24 h e pela limpeza semanal |
| **GitHub** | Nada do uso do app | Só entrega as atualizações do instalador |

O app **não** tem análise de uso, propaganda nem rastreadores, e **não** vende nem compartilha dados com ninguém além dos serviços da tabela.

## O que cada pessoa controla

- Quem usa o site pode sair a qualquer momento e pedir a remoção dos seus dados ao administrador.
- O administrador pode remover (bloquear) pessoas da equipe e usar **Limpar histórico**, que apaga o histórico do PC, do site e as mensagens do bot no Telegram com menos de 48 h.
- O Telegram só deixa o bot apagar mensagens com menos de 48 horas.

## Contato

Andrey Gutinik, responsável pelo app: abra uma *issue* em https://github.com/AndreyGutinik/Alerta-IBT-2026/issues.
