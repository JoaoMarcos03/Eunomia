# Fase 2: Application Shell

**Âmbito (27.1, transversal; 28):** janela principal com o layout da 5.1; registo de comandos com o inventário do HeidiSQL (anexo C §2) e menus, toolbar e keymap gerados a partir dele (itens futuros visíveis e desativados); temas claro e escuro por tokens; status bar de 7 painéis; SQL log; settings JSON e `app.db`; esqueleto de Preferences com Shortcuts.

**Requisitos:** FAM-L01, L02, L03, L06, L07, K01 (estrutura: atalhos por omissão, redefinição, conflitos, import do HeidiSQL fica para a Fase 3 com o import de settings).

**Ordem de trabalho:**
1. Portas de settings em `src/App`; `src/Infra.Settings` com JSON versionado e escrita atómica, e `app.db` (SQLite) com migrações.
2. Catálogo de comandos: 163 actions + 16 "Copy as" + atalhos de itens de menu; modelo de menus e toolbar pela ordem do anexo C; testes de inventário e de conflitos.
3. `src/Presentation`: buffer do SQL log (300 linhas, 2.000 caracteres, `/* */`, categorias), estado da status bar, dimensões de painéis.
4. `src/UI.Shell`: `MainWindow` migrada do `App.Desktop`, menus e toolbar gerados, keymap por contexto de foco, ícones vetoriais próprios, splitters persistidos, Display › Reset panel dimensions, toggles do menu Display.
5. Tokens de tema (`assets/themes`) aplicados ao shell e aos controlos; Preferences (General, Shortcuts com deteção de conflitos).
6. Testes headless (menus, teclado, reset de dimensões) e snapshots da janela em claro e escuro.

**Feito quando:** build e testes verdes; teste de inventário com as 163 actions mapeadas; menus pela ordem do anexo C; snapshot da janela por omissão em claro e escuro; reiniciar repõe as dimensões (teste); atalhos redefiníveis com aviso de conflito e persistidos.
