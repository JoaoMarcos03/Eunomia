# Fase 13: UX e UI (acabamento do MVP)

**Objetivo (pedido do utilizador a 8 out):** quem usa o HeidiSQL todos os dias troca para o Eunomia quase sem dificuldade. A UX (fluxos, atalhos, menus, comportamento dos cliques e do teclado) fica igual ou muito parecida; a UI pode ser modernizada. Botões pouco práticos ou quase não usados saem. O utilizador não vai fazer verificações manuais: tudo o que se puder testar, testa-se aqui.

**Regras desta fase:** a fidelidade ao HeidiSQL continua a vir da spec (secções 4–6, anexos C e D); "modernizar" é visual (espaçamentos, tipografia, ícones, cores, estados) e limpeza, nunca mudar onde as coisas estão nem o que um atalho faz.

**Ordem de trabalho:**
1. Inventário: snapshots de todos os ecrãs e diálogos (claro e escuro), lista dos comandos do catálogo sem handler e das lacunas que as fases deixaram ("Ficou por fazer" em `docs/progresso.md`).
2. Toolbar e menus: a toolbar só com o que se usa (sessões, ligar, refresh, nova query, correr, cancelar, ficheiros, navegação de dados, export/import, preferences); itens sem handler escondidos em vez de desativados; os menus mantêm a ordem do HeidiSQL.
3. Aspeto: tema claro e escuro coerentes, espaçamentos e tamanhos de letra, ícones, tabs, status bar, SQL log, grid e árvore; snapshots revistos um a um.
4. Fluxos do dia a dia, com testes headless de ponta a ponta: arrancar e ligar, árvore → tabs, Data tab (filtrar, ordenar, editar, inserir a seguir à linha focada), query tab (correr, erros, autocomplete, ficheiros), table editor (pergunta ao sair com alterações, arrastar colunas para índices), export/import, Schema Visualizer, Preferences.
5. Lacunas práticas que um utilizador do HeidiSQL nota: as que forem baratas implementam-se; as que não fizerem falta saem da UI.

**Feito quando:** build e testes verdes, todos os ecrãs com snapshot revisto, fluxos principais com teste de ponta a ponta, CI verde, push, `docs/utilizador/guia.md` atualizado.
