# Fase 14: multiplataforma, robustez e beta

**Objetivo (pedido do utilizador a 9 out):** o Eunomia corre em Windows, macOS e Linux; um erro do programa nunca corrompe nem perde dados de bases de produção; os problemas comuns dos clientes de BD estão verificados e cobertos por testes; a app passa de alpha a beta (1.0.0-beta.1). Corresponde à Fase 14 da secção 28 (Linux e macOS, libsecret e Keychain, packaging), feita antes da 13 da spec (PostgreSQL) por pedido do utilizador.

**Ordem de trabalho:**
1. Segurança dos dados na escrita: literais independentes do `NO_BACKSLASH_ESCAPES`, `LIMIT 1` em todas as escritas do grid, guardas contra gravar valores cortados ou binários convertidos, ALTER do table editor sem perder colunas, objetos (views, rotinas) substituídos sem janela sem objeto.
2. Transações (14.5): estado da transação da ligação primária, aviso ao fechar e ao religar com transação aberta, edições em lote numa transação própria.
3. Sessões de produção (23.2): modo read-only por sessão imposto pelo servidor e confirmação de escritas.
4. Ficheiros do utilizador: `sessions.json` com cópia de segurança e fusão entre instâncias; permissões 0600/0700 em Unix.
5. Multiplataforma: build sem caminhos do Windows, pastas por sistema (XDG, `~/Library`), segredos no Keychain (macOS) e Secret Service (Linux), fontes, atalhos com Cmd no macOS, SSH agent por socket Unix, CI com testes em Linux e macOS e publish NativeAOT para linux-x64/arm64 e osx-x64/arm64 com pacotes (tar.gz, .app).
6. Testes de robustez: round trip de dump com checksums em todos os servidores do CI, valores difíceis por tipo, fuzzing do lexer, splitter e CSV.
7. Beta: versão 1.0.0-beta.1, `docs/roadmap.md`, guia para macOS e Linux, CI verde, push.

**Feito quando:** build e testes verdes em Windows, Linux e macOS no CI; publish NativeAOT dos 5 alvos no CI; cada risco de dados da lista do passo 1–4 com teste; `docs/roadmap.md` e guia atualizados; push.
