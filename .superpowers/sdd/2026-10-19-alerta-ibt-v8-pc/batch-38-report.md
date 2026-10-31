# Lote 38 - cartao Holyrics em duas colunas equilibradas

- Novo painel `Theme/BalancedColumns.cs`: duas colunas (attached `Side`), uma coluna na ordem dos filhos quando < 2x320+16 px; o ultimo bloco da coluna menor estica para os fundos alinharem.
- ControlPage.xaml: HolyricsGrid e PresentationGrid viraram um unico BalancedColumns. Esquerda: endereco+porta, setas, modo apresentacao. Direita: token, teclas do controle. Todos x:Name, tooltips, bindings e linhas de status/motivo/dica mantidos.
- Testes: ControlLayoutTests e PresentationLayoutTests (colunas, fundos <= 8 px a 1100/1600, ordem em 1 coluna a 900).
- PNGs vistos (900/1100/1600): sem buracos. Ressalva: sem presentation (HasPresentation falso) o token estica; com muitas teclas o bloco "Modo apresentacao" fica alto.
