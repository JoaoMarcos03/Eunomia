# Eunomia: guia do utilizador

O Eunomia é um cliente de bases de dados MySQL e MariaDB para Windows, macOS e Linux, feito para quem usa o HeidiSQL: os menus, os atalhos, os diálogos e o formato dos ficheiros (SQL export, CSV) seguem os do HeidiSQL. As diferenças estão no fim deste guia.

Esta é a versão beta (1.0.0-beta.1). Ficam para depois, entre outras coisas: PostgreSQL, SQL Server e SQLite, User manager, Maintenance, Bulk table editor, Generate data, Copy table, Insert files, code folding, e export para outra base ou outro servidor. Os menus só mostram o que já funciona.

## Vindo do HeidiSQL

- **As sessões e as preferências**: no primeiro arranque, se o HeidiSQL estiver instalado neste Windows, o Eunomia pergunta se importa tudo o que tem equivalente: as sessões (pastas, cores, SSH, SSL, estatísticas, última base usada), os favoritos, os atalhos, as preferências (arranque, árvore, grid, editor SQL, log, tabs, export, tema claro ou escuro), o filtro, a ordenação e as colunas ocultas guardados de cada tabela, os filtros recentes e o histórico de queries. As passwords vão para o Credential Manager. O HeidiSQL não é alterado. Ficam de fora só as cores personalizadas do editor e do grid, a língua e o pack de ícones. Quase tudo se aplica logo; as tabs em várias linhas aplicam-se no arranque seguinte. Também há o botão **Import sessions from HeidiSQL** no Session Manager (Start e "More"). Um HeidiSQL portable guarda tudo no `portable_settings.txt`: esse ficheiro importa-se com **File › Import settings file**.
- **Onde estão as coisas**: a mesma janela (árvore à esquerda, tabs Host/Database/Table/Data/Query à direita, SQL log em baixo), os mesmos menus pela mesma ordem e os mesmos atalhos (F9, Ctrl+F9, Ctrl+Shift+F9, F5, F6, F7, Ctrl+T, Ctrl+E, Ctrl+D, Ctrl+Shift+F, F1, Ctrl+F8…). Os atalhos redefinidos no HeidiSQL vêm com o import.
- **A toolbar** tem só os botões do dia a dia: sessões, refresh, navegação e edição do grid, nova query, Run (com o Stop ao lado), Stop on errors, abrir e gravar ficheiros, Find e Replace, import/export e Preferences. O resto continua nos menus e nos atalhos.
- **O aspeto** é mais limpo: tema claro e escuro (Tools › Preferences › General › Theme, também a seguir ao Windows), a cor da sessão aparece como uma barra e um fundo suave na árvore, no editor e nos resultados, e a barra de título acompanha o tema.

## Instalar e arrancar

O executável é nativo (NativeAOT) em todos os sistemas: não precisa de .NET instalado.

**Windows** (10 ou 11, 64 bits):

- Pacote portable: descompactar a pasta e correr `Eunomia.exe`. Para guardar tudo ao lado do executável (pen USB), criar na pasta do executável um ficheiro `portable_settings.json` com o conteúdo `{}` (o pacote feito com `-Portable` já o traz): as preferências ficam nele, as sessões e o `app.db` na mesma pasta, e os journals, o spill e os logs na subpasta `data`. As passwords continuam no Credential Manager do Windows.
- No primeiro arranque abre o Session Manager.

**macOS** (12 ou mais recente; `osx-arm64` para Apple Silicon, `osx-x64` para Intel):

- Descompactar o `.zip` e arrastar o `Eunomia.app` para Aplicações.
- A aplicação não está assinada pela Apple: na primeira vez, abrir com o botão direito › Abrir (ou Definições do Sistema › Privacidade e Segurança › "Abrir mesmo assim"). Em alternativa, no Terminal: `xattr -dr com.apple.quarantine /Applications/Eunomia.app`.
- Os atalhos usam ⌘ onde o HeidiSQL usa Ctrl (⌘C, ⌘Enter, ⌘-clique para juntar à seleção); o Control continua a funcionar. As passwords ficam no Porta-chaves (Keychain), com o serviço "Eunomia".

**Linux** (x64 nativo; arm64 com runtime incluído):

- Descompactar o `.tar.gz` e correr `eunomia/Eunomia`, ou `eunomia/install.sh` para instalar no perfil (sem sudo): fica em `~/.local/opt/eunomia`, com atalho no menu e `~/.local/bin/Eunomia`.
- Precisa de um ambiente gráfico (X11, ou Wayland com XWayland).
- As passwords ficam no keyring do sistema pelo Secret Service (GNOME Keyring, KWallet 5.97+, KeePassXC). Sem keyring, o Eunomia avisa no arranque e guarda as passwords só até fechar: nunca as escreve em ficheiro.
- Os diálogos de abrir e gravar são os do sistema (`xdg-desktop-portal`); se não houver, aparece uma caixa para escrever o caminho.

### Onde ficam os dados

| O quê | Onde (instalação normal) |
| --- | --- |
| Preferências | `%APPDATA%\Eunomia\settings.json` |
| Sessões gravadas (sem passwords) | `%APPDATA%\Eunomia\sessions.json` |
| Histórico de queries, filtros, favoritos, layouts dos diagramas | `%APPDATA%\Eunomia\app.db` |
| Passwords | Windows Credential Manager (entradas `Eunomia/…`); chaves e segredos maiores em `%APPDATA%\Eunomia\secrets`, cifrados com DPAPI |
| Query tabs por gravar (restauro depois de um crash) | `%LOCALAPPDATA%\Eunomia\tabs` |
| Imports interrompidos (retoma) | `%LOCALAPPDATA%\Eunomia\jobs` |
| Resultados grandes despejados para disco | `%LOCALAPPDATA%\Eunomia\spill` (limpo no arranque) |
| Log de diagnóstico e crash reports | `%LOCALAPPDATA%\Eunomia\logs` |

No macOS tudo fica em `~/Library/Application Support/Eunomia`. No Linux as preferências, as sessões e o `app.db` ficam em `~/.config/Eunomia` (ou `$XDG_CONFIG_HOME`) e o resto em `~/.local/share/Eunomia` (ou `$XDG_DATA_HOME`); em ambos as pastas só são legíveis pelo próprio utilizador.

`settings.json` e `sessions.json` guardam a versão anterior em `.bak`: um ficheiro danificado (falta de luz, disco cheio) fica de lado com o sufixo `.corrupt-<data>` e o Eunomia volta à cópia anterior, avisando no SQL log. Duas janelas do Eunomia abertas ao mesmo tempo não apagam as sessões uma da outra: ao gravar, as alterações das duas juntam-se.

Nada sai da máquina: não há telemetria nem verificação de atualizações.

## Sessões

- **File › Session manager**: pastas, cores, filtro, New/Save/Delete/Duplicate/Rename, arrastar entre pastas e estatísticas por sessão.
- **Preferences › General**: "Automatically reconnect to previously opened sessions on startup" liga, no arranque, as sessões que estavam abertas ao fechar; "Restore last used database on startup" volta a selecionar a última base usada.
- **File › Import settings file**: importa o ficheiro de settings do HeidiSQL (sessões, pastas, atalhos, favoritos). As passwords vão para o Credential Manager; depois convém apagar o ficheiro exportado do HeidiSQL, que as tem quase em claro.
- SSH: o túnel é interno (não precisa do plink). A primeira ligação a um servidor SSH mostra a chave para confirmar; uma chave diferente depois bloqueia a ligação. Funciona com o agent do OpenSSH e com o Pageant.
- TLS: por omissão verifica a CA e o nome do servidor; níveis mais fracos têm de ser escolhidos à mão.
- Várias sessões abertas ao mesmo tempo, cada uma com a sua cor (na árvore, no editor e nos resultados).
- **Sessões de produção** (Session Manager › Advanced):
  - **Read-only session**: o Eunomia só envia leituras (SELECT, SHOW, EXPLAIN, USE, SET de variáveis da sessão…); qualquer instrução que mude dados ou estrutura é recusada antes de sair do PC, e o servidor também fica em modo só de leitura (`SET SESSION TRANSACTION READ ONLY`). O grid não edita e a barra da query tab diz "(read-only)".
  - **Ask before running statements that change data or structure**: para sessões que precisam de escrever, pergunta antes de correr escritas numa query tab, um Run SQL file ou um import de CSV, com o nome e o servidor da sessão.
- As perguntas de Drop, Empty e Delete rows mostram a sessão e o servidor, para não apagar no servidor errado.
- **Log queries to file** (Session Manager › Advanced, com "DDL queries" e/ou "DML queries"): as instruções que mudam estrutura ou dados (query tabs, edições do grid, table editor, Drop/Empty) ficam no ficheiro escolhido, uma por linha com a hora e a sessão, e as passwords mascaradas. Serve de registo do que se correu em produção.

## Transações

- Como no HeidiSQL, cada instrução grava sozinha (autocommit). `START TRANSACTION`/`BEGIN` numa query tab abre uma transação, que só fica gravada com `COMMIT`.
- Enquanto há uma transação aberta, a status bar mostra **Transaction open** ou, depois de alterações, **Uncommitted changes** (a laranja).
- Desligar a sessão ou fechar a janela com alterações por confirmar pergunta: **Yes** faz `COMMIT`, **No** faz `ROLLBACK`, **Cancel** não fecha.
- Gravar uma tabela, um objeto, ou fazer Drop/Empty nessa altura avisa que o servidor faz commit da transação antes do DDL.
- Se a ligação cair com alterações por confirmar, o Eunomia avisa que o servidor as desfez (não ficaram gravadas). Num deadlock, a query tab avisa que a transação inteira foi desfeita.
- **Delete selected rows** com várias linhas apaga todas ou nenhuma (numa transação própria).

## Árvore, Host tab e Database tab

- **Ctrl+D** vai para a árvore; **Ctrl+E** para o Table filter. O filtro aceita expressões regulares e `;` como "ou".
- Favoritos (estrela), grupos por tipo de objeto, colunas e tamanhos na árvore (Tree style options no menu da árvore).
- O Host tab mostra as bases (Databases), as variáveis (Variables: sessão e global lado a lado, a sessão destacada quando é diferente), o estado do servidor (Status, com médias por hora e por segundo), os processos (Processes: **Del** mata, "EXPLAIN query" no menu) e os contadores de comandos (Command-Statistics). O Database tab mostra os objetos da base selecionada.
- **F5** refresca o nó da árvore; com o foco numa lista do Host ou do Database tab, refresca só essa lista. **Shift+F5** faz o refresh completo com as estatísticas.
- **Auto refresh** (**Ctrl+F5**, ou o dropdown do botão Refresh): relê a lista do tab ativo a cada 10 s; "Set interval ..." muda o intervalo. Útil para vigiar os processos.
- **Find text on server** (**Ctrl+Shift+F**): procura um texto (ou um predicado SQL, como `> NOW()`) em todas as colunas das tabelas escolhidas, com filtro por tipo de coluna, wildcards, regex e maiúsculas. Mostra as linhas encontradas por tabela; **See results** abre uma query tab com os SELECT.
- **Maintenance** (Tools e menu da árvore): Check, Analyze, Checksum, Optimize e Repair nas tabelas e views escolhidas (as escolhidas na árvore ou na lista vêm marcadas), com as opções que cada operação aceita (Quick, Fast, Medium, Extended, Changed, Use FRM file, For Upgrade). O resultado do servidor aparece por tabela, com os erros a vermelho e as notas a amarelo; Close passa a Cancel enquanto corre. Optimize e Repair reconstroem e bloqueiam a tabela: perguntam antes, com a sessão e o servidor. Numa sessão read-only só o Checksum corre.

## Data tab

- Mostra a tabela focada na árvore em blocos; **Ctrl+Alt+PgDn** (Next) e **Ctrl+Alt+End** (Show all) para ler mais.
- Uma tabela grande ou um servidor lento não prendem a janela: escolher outra coisa na árvore, ou correr uma query na mesma sessão, cancela o carregamento em curso (o servidor pára a query). **Esc** (Cancel running operation) também. Num Show all cancelado ficam as linhas que já chegaram, e Next e Show all continuam a partir daí.
- Ordenação por clique no cabeçalho, filtro WHERE (com histórico e autocomplete), Quick Filter no menu da célula, filtro local, multi column filter (**F7**).
- Editar: escrever na célula; a linha grava ao mudar de linha ou com **Ctrl+Enter**; **Esc** desfaz. **Ctrl+Ins** duplica sem chaves, **Ctrl+Del** apaga as linhas selecionadas. Insert value ▸ (data/hora, GUID, NULL com **Ctrl+Shift+N**, função com **Ctrl+F2**). ENUM, SET, datas, chaves estrangeiras e texto longo têm editores próprios.
- Cada gravação muda no máximo uma linha (`LIMIT 1`) e confirma quantas linhas mudaram. Sem chave primária nem índice único, o Eunomia usa todas as colunas e compara o texto de forma exata (uma linha `'Ana'` nunca é confundida com `'ana'`).
- Se outra pessoa mudou a linha no servidor depois de o Eunomia a ter lido, gravar pergunta antes de sobrescrever ("Row changed on the server"): OK grava por cima, Cancel deixa a edição por gravar (Esc desfaz, F5 mostra a linha atual).
- Números escrevem-se com ponto decimal e sem separadores de milhares (`1234.5`); um valor que não é um número numa coluna numérica é recusado com uma mensagem, em vez de o servidor o converter em silêncio. Para uma expressão SQL, **Ctrl+F2**.
- O que se copia, duplica e exporta é sempre o valor exato do servidor: DOUBLE com todos os dígitos, BIT em binário (`00001010`), binários completos, mesmo que o grid mostre menos.
- Copiar: **Ctrl+C**, e "Copy as" em 16 formatos; Export grid rows para ficheiro.
- **Follow Foreign Key** (menu da célula): abre a tabela referenciada no Data tab, já filtrada pela chave da linha. **Open URL** abre no browser uma célula com um endereço http/https.
- **Save BLOB to file** (menu da célula e painel do preview): grava o valor completo da célula num ficheiro, mesmo que o grid mostre só o início. Binários vão tal como estão (o nome sugerido é o da coluna, com `.png`, `.jpg`, `.gif` ou `.bmp` se for uma imagem, senão `.bin`); texto vai em UTF-8 com `.txt`.
- **Image preview** (Grid view options): painel por baixo da árvore com a imagem (JPEG, GIF, BMP ou PNG) da célula focada no Data tab ou num resultado, com o tamanho e o formato; reduz a imagem para caber, sem a ampliar. Imagens acima de 16 MiB não são mostradas.
- Linhas novas (**Ins**) aparecem no fim do grid (no HeidiSQL aparecem a seguir à linha focada); depois de gravar e recarregar ficam na ordem do servidor.
- **Preferences › Grid formatting**: fonte e tamanho do grid, largura máxima das colunas, linhas por página e máximo, e quanta memória os resultados podem usar antes de ir para disco.

## Query tabs

- **Ctrl+T** abre uma tab nova, **Ctrl+F4** fecha. As tabs voltam depois de fechar ou de um crash (com o texto até cerca de 2 s antes).
- **F9** corre tudo, **Ctrl+F9** a seleção, **Ctrl+Shift+F9** a instrução onde está o cursor. "Send batch in one go" e "Stop on errors" no menu SQL.
- Cada tab corre na sessão da árvore, ou na sessão a que foi fixada ("Pin tab to session" no menu da tab). Cancelar (o botão vermelho) só pára a query desta tab.
- Autocomplete com **Ctrl+Space** (tabelas, colunas, funções, palavras-chave, variáveis), Find/Replace com **Ctrl+F**/**Ctrl+R** e **F3**, comentar linhas, **Ctrl+U**/**Ctrl+L** para maiúsculas/minúsculas, **Alt+↑/↓** para mover linhas.
- Resultados em tabs (**Alt+←/→**), com ordenação e filtro locais. Um resultado de uma só tabela com chave pode ser editado como no Data tab.
- Helpers (à direita): colunas, funções, palavras-chave, snippets e histórico.
- **Explain current query** (menu Query): corre `EXPLAIN` da instrução onde está o cursor. **Reformat SQL** (**Ctrl+F8**): arruma a seleção ou o texto todo (cláusulas em linhas, AND/OR indentados), num só undo. **F1** abre a ajuda do próprio servidor na palavra do cursor (SQL help).
- **Code folding** (menu Query ▸ Code folding, ligado por omissão): o texto entre uma linha `#region nome` e uma linha `#endregion` dobra-se pela margem [+]/[−] (o `#` é um comentário no MySQL, o SQL não muda). **Insert region start/end marker** escreve os marcadores na linha do cursor e **Fold selection** envolve as linhas escolhidas numa região já dobrada (um Ctrl+Z desfaz). A margem só aparece quando o texto tem regiões.
- Ficheiros: **Ctrl+O** abre, **Ctrl+S** grava, **F12** grava como, e a lista dos 20 recentes. Um ficheiro alterado fora do Eunomia pergunta se deve recarregar.

## Table editor e outros objetos

- Alt+Enter numa tabela (ou Create new ▸ Table): colunas, índices, chaves estrangeiras, checks, opções e o código CREATE/ALTER antes de gravar. Save aplica o ALTER calculado pela diferença.
- Antes de gravar alterações que podem perder dados (apagar colunas, estreitar um tipo, mudar o charset), o Eunomia mostra a lista e pede confirmação. O ALTER corre em modo estrito: um valor que não caiba no tipo novo faz a gravação falhar, em vez de ser cortado. Se o ALTER falhar a meio, as chaves estrangeiras que já tinham sido largadas voltam a ser criadas.
- **Views, procedures, functions, triggers e events** abrem no Table tab com o `CREATE` editável (em vez dos formulários do HeidiSQL). **Save** substitui o objeto sem o deixar desaparecer: views (e, no MariaDB, também rotinas, triggers e events) com `CREATE OR REPLACE`; no MySQL as rotinas são validadas primeiro com um nome temporário. Se mesmo assim o objeto antigo não puder voltar, o código dele fica num ficheiro na pasta `recovery` (ao lado dos logs) e o erro diz onde. **Create new ▸ View / Stored procedure / Stored function / Trigger / Event** começa por um modelo.
- **Create new ▸ Table copy** (numa tabela ou view): cria `<nome>_copy` (ou o nome e a base que escolher) com as colunas, índices e chaves estrangeiras marcados e, se quiser, os dados, com um filtro WHERE opcional; tudo corre no servidor. O destino tem de ser uma tabela nova: se o nome já existir, o diálogo recusa (no HeidiSQL oferecia apagar a tabela existente; aqui faz-se Drop à parte). Numa cópia na mesma base, as chaves estrangeiras e os checks ficam com nomes novos (`<destino>_ibfk_1`, `<destino>_chk_1`), porque esses nomes não se podem repetir na base.
- Mudar de objeto na árvore ou fechar a janela com alterações por gravar pergunta "Save modified table/view/… ?" (Yes grava, No descarta, Cancel fica).

## Export e import

### Export database as SQL (Tools)

- Escolher bases e objetos na árvore do diálogo (com filtros), Drop/Create das bases e das tabelas, os dados (No data, Delete + insert, Insert, Insert ignore, Replace) e o Max INSERT size.
- Output: um ficheiro `.sql`, um `.zip`, um diretório com um ficheiro por objeto, ou o clipboard (até 64 MiB).
- **Consistent snapshot**: tudo numa transação, como o `mysqldump --single-transaction`. Sem ele, as tabelas são exportadas em paralelo.
- O ficheiro escreve-se como `.partial` e só fica com o nome final no fim; um cancel ou erro apaga-o.

### Run SQL file

- **Run SQL file…** (menu SQL) corre ficheiros sem os abrir no editor. No **Load** (Ctrl+O), ficheiros com mais de 5 MiB perguntam se é para correr diretamente.
- O ficheiro é lido aos bocados (memória constante) e o `DELIMITER` mantém-se entre blocos.
- As instruções do ficheiro não vão para o SQL log; o log mostra o resumo e cada erro com o número da instrução e a linha.
- Se parar num erro (Stop on errors) ou for cancelado, **Resume** continua na instrução que falhou. Um import interrompido por um crash aparece no arranque seguinte com a opção de retomar ou descartar. Se o ficheiro mudou entretanto, não retoma.

### Import CSV file (Tools)

- Defaults do HeidiSQL: `;` entre campos, `"` à volta (opcional), `"` como escape, `\r\n` no fim das linhas, ignorar 1 linha, REPLACE nos duplicados.
- **Server parses file contents**: `LOAD DATA LOCAL INFILE` com os dados enviados pelo Eunomia (o servidor não lê ficheiros do PC). O MySQL 8 traz o `local_infile` desligado; nesse caso, ou se usa o modo cliente, ou se liga no servidor com `SET GLOBAL local_infile = 1`.
- **Client parses file contents**: o Eunomia lê o CSV e envia INSERTs. Trata `""` dentro de aspas e `\N` / `NULL` como NULL.
- O campo k do ficheiro vai para a k-ésima coluna marcada (Up/Down mudam a ordem).

## Schema Visualizer

- No menu da árvore (base ou tabela): **Show in schema visualizer**. Abre o tab "Diagram: base".
- Tabelas com as colunas (chave primária, única e estrangeira, NULL), relações com a notação crow's foot (1:N e 1:1).
- Roda do rato: zoom; arrastar o fundo (ou espaço + arrastar, ou as setas): mover; **F** ajusta tudo ao ecrã, **0** volta a 100%.
- Clique seleciona, Ctrl+clique junta; as relações da seleção ficam realçadas. Arrastar move as tabelas (Snap alinha a uma grelha).
- Clicar numa coluna de chave estrangeira ou numa relação centra a tabela referenciada. Duplo clique abre o Data tab; **Alt+Enter** abre o table editor; o menu do nó tem ainda Select top 1000 rows e Copy name.
- Pesquisa com a sintaxe do Table filter: o que não corresponde fica esbatido; **Enter** seleciona os resultados.
- **Hide**, **Only selected** e **Show all** escolhem as tabelas visíveis. Bases com mais de 300 tabelas abrem só o grupo ligado à tabela escolhida.
- **F5** relê o schema: as posições ficam, as tabelas novas aparecem marcadas e as removidas são contadas na barra. O layout fica guardado por sessão e base.
- **Export PNG**: a vista atual ou o diagrama inteiro.
- Com "Sync with tree" ligado, selecionar uma tabela no diagrama seleciona-a na árvore e vice-versa.

## Diferenças face ao HeidiSQL

- **Paleta de comandos** (**Ctrl+Shift+P**, ou Tools ▸ Command palette): escreve parte do nome de um comando ("new query", "export", "wrap") e Enter corre-o. Só mostra os comandos que podem correr naquele momento, com o atalho ao lado. Os menus e atalhos do HeidiSQL ficam iguais.
- Nada bloqueia a janela: queries, exports e imports correm em ligações próprias, com cancel. Cancelar numa tab só pára essa tab.
- As tabs, os imports e o layout dos diagramas sobrevivem a um crash.
- Passwords só no cofre do sistema (Credential Manager, Keychain ou Secret Service); o SQL log mostra `IDENTIFIED BY '***'` em vez da password e o histórico de queries não guarda instruções com passwords.
- Uma ligação a um servidor remoto sem TLS nem túnel SSH deixa um aviso no SQL log; o tooltip do servidor na status bar diz se a ligação vai cifrada ("Encryption").
- Edições seguras com qualquer `sql_mode` do servidor (incluindo `NO_BACKSLASH_ESCAPES`), sessões read-only, aviso de transações abertas e confirmação antes de perder dados no table editor.
- O SQL export, o Run SQL file e o CSV import usam memória constante; o ZIP comprime enquanto escreve.
- O modo servidor do CSV import não dá ao servidor acesso a ficheiros do PC.
- Schema Visualizer (não existe no HeidiSQL).
- Views e rotinas editam-se pelo `CREATE` (sem os formulários do HeidiSQL); se o código novo falhar, o objeto antigo é reposto.
- Find text on server e Maintenance têm diálogos próprios (no HeidiSQL são tabs do "Table tools"); Optimize e Repair perguntam antes de correr.
- Não há formatters online no Reformat SQL (o SQL não sai da máquina) nem links para sites.

## Problemas

- **"Loading local data is disabled"** no CSV import: ver o modo servidor acima.
- **A chave SSH do servidor mudou**: a ligação é recusada. Se a mudança era esperada (servidor reinstalado), apague a linha desse servidor no ficheiro `known_hosts` da pasta das preferências (ver "Onde ficam os dados"); na ligação seguinte a chave nova é mostrada para confirmar. Se não a esperava, não ligue.
- **"Session … is read-only"**: a sessão está marcada como só de leitura (Session Manager › Advanced).
- **macOS diz que a aplicação "está danificada" ou "não pode ser aberta"**: ver a instalação no macOS (a quarentena do Gatekeeper).
- **Linux: "No system keyring"** no arranque: instale e desbloqueie o GNOME Keyring ou o KWallet; até lá as passwords não ficam gravadas.
- **Crash**: no arranque seguinte, o SQL log diz que houve um crash e onde está o relatório (`%LOCALAPPDATA%\Eunomia\logs\crash-….json`). O relatório não tem SQL, valores, servidores nem utilizadores. As query tabs voltam sozinhas.
- **A janela fica presa**: o log de diagnóstico (`diagnostic.jsonl`, na mesma pasta) regista cada bloqueio da janela com mais de 250 ms.
