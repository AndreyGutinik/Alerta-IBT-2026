# Projeto: Alerta-IBT

## Objetivo

Aplicativo desktop (Windows) que exibe avisos no telão da igreja numa barra horizontal animada no topo do LED 2560x768. Os avisos chegam pelo Telegram (e, em planos futuros, pelo site e por um app de Android) ou são digitados no PC. A barra não aparece na transmissão (OBS/NDI/print).

## Estado atual

- **Versão 8.0.0 (C#, .NET 10 + WPF)**, branch `v8-csharp`: app do PC completo (Plano 1), aguardando teste na igreja. Veja `docs/CHECKLIST-IGREJA.md`.
- Versão 7.0.0 (Electron): substituída. O código está na tag `v7.0.0`.
- Ainda não feito: Firebase, site PWA, conector do PC com o Firebase, APK (veja `docs/PROXIMOS-PASSOS.md`).

## Stack

C#, .NET 10, WPF, WPF-UI, CommunityToolkit.Mvvm, Microsoft.Extensions.Hosting, Serilog, Hunspell (pt-BR), Velopack, xUnit v3. Detalhes e comandos em `CLAUDE_CONTEXT.md`.

A regra antiga "não trocar a stack" (versão Electron) foi **revogada** pelo dono do projeto em 2026-10-19.

## Nome do app e do instalador

App: Alerta-IBT. Pacote Velopack: `AlertaIBT` (instalador `AlertaIBT-win-Setup.exe`), publicado nas Releases de `AndreyGutinik/Alerta-IBT-2026`.

## Funcionalidades

- Barra no topo (ou rodapé) do monitor 2560x768, fora da captura, rolagem lisa, entra e sai animada.
- Fila com urgente, pular, reordenar, remover, histórico de 50, "exibir de novo"; salva em disco.
- Telegram por long polling (grupo, prefixo `#`), com respostas no grupo (exibido, faltou campo, repetido, LED desligado, expirado, pausa).
- Formato `MODELO COR | PLACA: ABC-1234 | MOTIVO`, placas antiga e Mercosul, aviso livre.
- Correção: Gemini grátis com trava de segurança, corretor offline (Hunspell), regras fixas do usuário.
- Painel rápido na bandeja (clique e atalho global), janela com Início, Correção e Configurações, autosave.
- API local `127.0.0.1:8787` (`/overlay/status|on|off`) para o Holyrics pausar na pregação.
- Início com o Windows sempre; instância única; atualização automática pelas Releases.

## Regras importantes

- Preservar o que a equipe da igreja já usa: prefixo `#` no Telegram, textos de resposta, rotas da API do Holyrics.
- Nada de segredo em texto puro, em log ou no git.
- Mudanças pequenas, com teste, e build com 0 avisos.
- Sem push, sem publicar Release e sem mexer no Firebase sem o pedido do dono.

## Arquivos de dados do app instalado

`%AppData%\AlertaIBT`: `settings.json`, `queue.json`, `logs\`.
