# Assinatura do instalador (SignPath Foundation)

> **Status em 2026-10-22:** a **licença MIT já foi adicionada** (`LICENSE`) e a **política de privacidade** está em [PRIVACIDADE.md](PRIVACIDADE.md). Falta, nesta ordem: (1) ativar o MFA no GitHub; (2) publicar uma versão **estável** (hoje só há pré-releases) e ter a página de download (o README serve); (3) acrescentar ao README a seção "Code signing policy" com o texto exato de https://signpath.org/terms (só depois de o pedido ser aprovado, porque ela cita o SignPath); (4) enviar o formulário em https://signpath.org/apply e confirmar o e-mail. A aprovação é decisão do SignPath, leva semanas e já recusou projetos pequenos por pouca visibilidade.

Guia curto para o dono. Nada aqui foi feito ainda: nenhuma conta criada, nenhum formulário enviado, nenhuma etapa de assinatura no workflow de Release. As informações abaixo foram lidas nas páginas do SignPath em 2026-10-21; **as regras são deles e mudam**, então reconfira em https://signpath.org/terms antes de se inscrever.

## Por que o Windows mostra o aviso

O instalador (`Setup.exe`) e o `AlertaIBT.exe` não têm **assinatura digital**. Sem ela, o Windows SmartScreen mostra "O Windows protegeu o computador" na primeira instalação (e o Defender pode desconfiar mais). Dá para instalar com **Mais informações > Executar assim mesmo**, e o aviso some sozinho com o tempo, conforme muita gente instala o mesmo arquivo. A assinatura diz ao Windows quem publicou o arquivo e que ele não foi alterado.

## A opção grátis: SignPath Foundation

A SignPath Foundation dá certificado de assinatura grátis para projetos de código aberto: ela verifica que o binário foi construído a partir do repositório aberto e "garante" isso com o nome dela. A chave privada fica num HSM deles. Detalhe importante: **o certificado sai no nome "SignPath Foundation"**, não no seu nome nem no da igreja. O aviso do SmartScreen só diminui com o tempo e com o número de instalações; assinar não o apaga de imediato.

### Exigências (https://signpath.org/terms)

- Licença de código aberto **aprovada pela OSI**, sem licença comercial dupla em nenhum componente. **Feito: MIT.**
- Nenhum componente proprietário (fora bibliotecas do sistema), e nenhum malware ou programa potencialmente indesejado.
- Projeto **mantido** e **já lançado** na forma que será assinada (o instalador já publicado).
- Funcionalidade descrita na página de download.
- Só assinar o que a própria equipe constrói a partir do próprio código.
- Sem ferramentas de invasão; sem mexer na configuração do sistema do usuário sem avisar; quem tem instalador precisa oferecer desinstalação (o Velopack já oferece).
- **Privacidade:** software que envia dados do usuário a outros sistemas precisa dizer isso numa política de privacidade, mostrar na instalação e permitir desligar. O IBT Controller envia o texto dos avisos ao Google (Gemini, desligável em Início > Correção) e conversa com o Telegram: isso precisa estar escrito.
- **Autenticação em dois fatores** (MFA) para todos da equipe, no SignPath e no repositório (GitHub).
- Definir **autores**, **revisores** e **aprovadores** (quem aprova cada pedido de assinatura). No seu caso, provavelmente você nos três papéis.
- A página inicial do projeto (o README serve) precisa ter uma seção chamada **"Code signing policy"** com o crédito exigido ao SignPath e à SignPath Foundation, os papéis da equipe e a política de privacidade.
- Nome do produto e versão gravados nos binários (já existe `Product` e `Version` no `.csproj`; falta conferir `Company`).
- Cada versão exige **aprovação manual** no SignPath antes de assinar.
- A SignPath Foundation faz uma análise inicial de **reputação e controle** do projeto; a aceitação é decisão deles, sem recurso.

### Tempo de resposta e chance real

O formulário não publica prazo. Relatos de quem se inscreveu: análise manual de 1 a 2 semanas até mais de 3 semanas, com perguntas extras. Um projeto pequeno (https://github.com/getdictus/dictus-desktop/issues/38) foi **recusado por pouca visibilidade pública** depois de ~7 semanas (pode se inscrever de novo depois). Ou seja: um app de uso interno de uma igreja, com poucos downloads e sem menções públicas, pode ser recusado. Vale tentar, mas sem depender disso.

### Formulário (https://signpath.org/apply)

A página de inscrição é montada por script e não deu para ler os campos automaticamente. Pelo relato de quem preencheu, ele pede: repositório e página do projeto, uma URL de download que cite o SignPath, URL da política de privacidade, descrição curta do projeto, informações de reputação (menções, downloads, estrelas), sistema de build (GitHub Actions) e algumas caixas de confirmação obrigatórias. Confira na tela.

### Como assinar com o nosso fluxo (Velopack + GitHub Actions)

- O SignPath tem a ação oficial `signpath/github-action-submit-signing-request`. O fluxo: `actions/upload-artifact` (v4 ou mais novo) envia o arquivo sem assinar, a ação envia o pedido ao SignPath (precisa de `api-token`, `organization-id`, `project-slug`, `signing-policy-slug` e do `github-artifact-id`) e baixa o arquivo assinado. Para projetos de código aberto, **todas as etapas até o pedido precisam rodar em agentes hospedados do GitHub** (o `windows-latest` do nosso workflow serve). O "GitHub.com" entra como *trusted build system* no SignPath.
- O `vpk` (Velopack) assina sozinho com `--signParams` (signtool), `--signTemplate` (qualquer ferramenta de linha de comando) ou `--azureTrustedSignFile` (Azure). **O SignPath não é citado na documentação do Velopack**, e o Velopack diz que a assinatura precisa acontecer nele porque o `Setup.exe` e o `Update.exe` são assinados em momentos diferentes da montagem. Não está documentado se um `AlertaIBT.exe` já assinado é aceito sem problema pelo `vpk pack` no Windows.
- Caminho provável (a **testar num repositório de brinquedo antes**): (1) publicar o app; (2) enviar `AlertaIBT.exe` e as DLLs ao SignPath e baixar assinados; (3) `vpk pack` com os arquivos já assinados; (4) enviar o `Setup.exe` ao SignPath num segundo pedido. O `Update.exe` embutido no pacote pode ficar sem assinatura, e a assinatura do `Setup.exe` é a que o SmartScreen mostra primeiro. Como o SignPath assina por artefato (e não por arquivo, como o `--signTemplate` espera), o `--signTemplate` não encaixa direto.
- Não mexemos no workflow de Release nesta rodada. Só depois da aprovação.

### Outras opções (se o SignPath recusar)

- **Azure Artifact Signing** (antigo Trusted Signing): o `vpk` assina direto com `--azureTrustedSignFile`, mas é pago e depende de verificação de identidade. Confira preço e disponibilidade no seu país.
- **Certificado "Open Source" de uma autoridade comercial** (por exemplo Certum), com custo anual, e que também exige projeto aberto. Um projeto recusado pelo SignPath relatou emissão em 1 a 5 dias; confira valores atuais.
- Não assinar e manter o roteiro "Mais informações > Executar assim mesmo" (o que já está no CHECKLIST-IGREJA).

## Licença: decisão sua, antes de tudo

O SignPath exige licença OSI. O repositório **não tem LICENSE** e eu **não adicionei nenhuma**: escolher a licença é decisão do dono (por exemplo MIT, que é curta e permissiva; ou GPL, que obriga quem copiar a manter o código aberto). Ao publicar sob uma licença, qualquer pessoa pode usar, copiar e (conforme a licença) vender o código; isso não dá para desfazer para as cópias já feitas. Também confira se as bibliotecas usadas (WPF-UI, Velopack, Hunspell/VERO do dicionário em LGPL/MPL, etc.) permitem a licença escolhida. Sem licença escolhida, o pedido ao SignPath não vale a pena.

## Texto pronto para o formulário (em inglês, o formulário é em inglês)

> **IBT Controller** (formerly Alerta-IBT) is a free, open-source Windows desktop app (C#, .NET 10, WPF) used by a local church (IBT) to show short parking notices ("Silver Corolla ABC-1D23, lights on") as a scrolling bar over an LED screen during services. Notices come from members of a private Telegram group, a local PC panel and a local HTTP API; the text is spell-checked offline with the Hunspell pt-BR dictionary (VERO) and, optionally, by the Google Gemini free tier. The app runs from the system tray, is installed and updated with Velopack, contains only our own code plus open-source libraries, does not change system settings beyond an optional "start with Windows" entry, and can be uninstalled from Windows Apps & Features. The only data sent out is the notice text (to Telegram and, if enabled, to Google Gemini); this is described in the privacy policy and Gemini can be turned off in Settings. Repository: https://github.com/AndreyGutinik/Alerta-IBT-2026 . Builds: GitHub Actions on GitHub-hosted runners (`.github/workflows/release.yml`). Reputation: [owner: fill in real numbers, such as installs, releases and any public mentions].

(Ajustar a verdade antes de enviar: nome exato da igreja, se o site de download existe e os números reais de uso. Não escreva "reputação" que não existe.)

## O que só o dono pode fazer

1. **Escolher a licença** (ex.: MIT) e dizer ao Claude para adicionar o `LICENSE` e a nota no README.
2. Criar uma política de privacidade curta (o Claude pode escrever o rascunho) e publicar no repositório ou no site.
3. Ativar **MFA** na conta do GitHub (e depois na do SignPath).
4. Publicar uma versão estável e uma página de download que descreva o app e **cite o SignPath** (o README já pode servir).
5. Adicionar ao README a seção **"Code signing policy"** com o texto exato exigido em https://signpath.org/terms (crédito ao SignPath e à SignPath Foundation, papéis e privacidade).
6. **Enviar o formulário** em https://signpath.org/apply com o texto acima e **confirmar o e-mail** que o SignPath mandar. Responder a perguntas de reputação (downloads, menções).
7. Depois da aprovação: criar a organização/projeto no SignPath, ligar o "GitHub.com" como *trusted build system*, guardar `SIGNPATH_API_TOKEN` e os identificadores em **Secrets** do GitHub, e só então pedir ao Claude para ligar a assinatura no workflow (testando antes num repositório de brinquedo).
8. A cada versão: **aprovar** o pedido de assinatura no SignPath.
