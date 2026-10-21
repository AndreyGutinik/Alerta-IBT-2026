# Dictionaries

`pt_BR.aff` and `pt_BR.dic` are the Brazilian Portuguese Hunspell dictionary **VERO** (Verificador Ortografico
Livre), version 3.2, by Raimundo Santos Moura and team. They are used as shipped (byte for byte, UTF-8 with BOM) by
`OfflineCorrector`.

- Source: https://github.com/LibreOffice/dictionaries/tree/master/pt_BR
  - https://raw.githubusercontent.com/LibreOffice/dictionaries/master/pt_BR/pt_BR.aff
  - https://raw.githubusercontent.com/LibreOffice/dictionaries/master/pt_BR/pt_BR.dic
- LibreOffice/dictionaries commit (latest one touching `pt_BR/` when downloaded): `1e848fbddd7fd8e03fb696ecc03ee1068fab141c`
  (2026-06-13, "Add rule for pt-BR pleonasms")
- Downloaded on: 2026-10-20
- License (from `pt_BR/README_pt_BR.txt` of the same repository): "This dictionary is under development by Raimundo
  Moura and his team. It is licensed under the terms of the GNU Lesser General Public License version 3 (LGPLv3), as
  published by the Free Software Foundation and the Mozilla Public License as published by the Mozilla Foundation."
  Credits: http://pt-br.libreoffice.org/projetos/projeto-vero-verificador-ortografico/

The files are marked `binary` in `.gitattributes` so Git never converts their line endings.
