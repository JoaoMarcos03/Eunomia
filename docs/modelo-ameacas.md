# Modelo de ameaças

Estado a 9 out 2026 (1.0.0-beta.1; roadmap 4.3). Complementa `docs/auditoria-seguranca.md` (o que foi revisto e corrigido) com o porquê das decisões: o que se protege, de quem, em que fronteiras e com que mitigação. Uma alteração numa das áreas da secção 5 revê a linha correspondente neste documento no mesmo PR.

## 1. O que se protege

| Ativo | Onde está | Pior caso |
| --- | --- | --- |
| Dados das bases do utilizador (produção) | No servidor | Alterados ou apagados sem o utilizador pedir (prioridade máxima, secção 2 da spec) |
| Passwords das sessões e do SSH, passphrases | Cofre do sistema (`Infra.Secrets.*`); em memória sem cofre | Lidas por outro processo ou enviadas para fora |
| Metadados das sessões (hosts, utilizadores, portas, bases) | `sessions.json`, `settings.json` | Expostos a quem lê o perfil do utilizador |
| SQL e valores escritos ou lidos | Journal das query tabs, histórico no `app.db`, spill, SQL log, log de queries | Copiados para fora da máquina ou de outro utilizador |
| O PC do utilizador | — | Um servidor, um ficheiro ou um export que corre código ou lê ficheiros locais |

## 2. De quem

- **Servidor de base de dados malicioso ou comprometido:** controla nomes de objetos, valores, mensagens de erro, o texto das outras sessões (`SHOW PROCESSLIST`) e os pedidos do protocolo (por exemplo `LOAD DATA LOCAL`).
- **Rede entre o cliente e o servidor:** lê ou altera o tráfego sem TLS nem SSH; faz-se passar pelo servidor ou pelo host SSH.
- **Ficheiros de origem desconhecida:** scripts SQL, CSV, exports de settings do HeidiSQL, ficheiros de sessões.
- **Outro processo ou utilizador local:** lê os ficheiros do perfil; vê a linha de comandos dos processos.
- **O próprio utilizador por engano:** corre uma escrita na sessão errada ou em produção.

Fora do modelo: um atacante com controlo total da conta do utilizador ou do sistema (lê o cofre e a memória), e o próprio servidor quando é ele que o utilizador quer alterar.

## 3. Fronteiras de confiança

```text
Ficheiros importados ──► [ Eunomia ] ◄──► rede (TLS / SSH) ◄──► servidor
                            │   ▲
     cofre do sistema ◄─────┤   └── clipboard, linha de comandos
     disco do utilizador ◄──┤
     browser / explorador ◄─┘ (Open URL, Explore folder, exports abertos noutros programas)
```

Tudo o que chega do servidor, de ficheiros, do clipboard ou da linha de comandos é dado, nunca SQL nem código: entra nas instruções só como literal ou identificador do dialeto (`CONTRIBUTING.md`, secção 3).

## 4. Ameaças por área

| Área | Ameaça | Mitigação | Verificado em |
| --- | --- | --- | --- |
| Escritas nos dados | UPDATE/DELETE que apanha outras linhas; valor cortado ou convertido; escaping que muda com o `sql_mode` | `RowWriter` com chave ou comparação exata e `LIMIT 1`, verificação das linhas afetadas; texto bruto sem perdas (`FormatRaw`); literais iguais em qualquer `sql_mode`; deteção de conflitos; o Copy table nunca escreve numa tabela que já existe (CREATE sem `IF NOT EXISTS`, o INSERT só corre depois dele) | `DataSafetyTests`, `RowWriterTests`, `TableCopyTests` |
| DDL da aplicação | ALTER que perde colunas ou corta valores; lock de metadata que pára produção; objeto apagado sem substituto | Confirmação das perdas, ALTER em modo estrito, `SafeDdl` com `lock_wait_timeout` curto, `CREATE OR REPLACE` ou reposição do original; OPTIMIZE e REPAIR do Maintenance (reconstroem e bloqueiam a tabela) só depois de confirmar, com a sessão e o servidor | `DdlComposerTests`, `ObjectActionsTests`, `TableMaintenanceTests` |
| Sessões de produção | Escrita por engano na sessão errada | Sessão read-only imposta pelo servidor e pela `ReadOnlyPolicy` (lista de leituras permitidas); "Ask before running statements that change data"; a sessão e o host em cada pergunta de Drop/Empty/Delete | `ReadOnlySessionTests` |
| SQL montado com texto de fora | Injeção pelo `INFO` de outra sessão, pelo clipboard ou por nomes de objetos | Identificadores e literais pelo dialeto; EXPLAIN de processos só com uma instrução explicável; filtros com mais de uma instrução recusados | Auditoria da Fase 14 (`docs/progresso.md`) |
| TLS | Escuta ou alteração do tráfego; servidor falso | Com SSL ligado, verificação do CA e do hostname por omissão; a ligação diz se vai cifrada e avisa nos remotos sem TLS nem SSH | `SessionHostTests` |
| SSH | Host falso ou chave trocada | `known_hosts` próprio; chave mudada bloqueia, chave de outro tipo pergunta com aviso; túnel só em `127.0.0.1`; agent por pipe/socket do sistema | `KnownHostsTests`, `SshAgentTests`, `SshTunnelTests` |
| `LOAD DATA LOCAL` | O servidor pede um ficheiro qualquer do PC | Só na ligação dedicada do import CSV, fechada no fim; o MySqlConnector só entrega o stream registado e recusa pedidos por nome sem servidor verificado; o cancel não deixa linhas a meio | `CsvImportTests` |
| Ficheiros importados | Script SQL ou CSV que corre escritas sem o utilizador ver; parser que rebenta com entradas patológicas | O Run SQL file e o import passam pelas sessões read-only e pela pergunta de escritas; splitter, lexer e CSV com fuzzing determinístico; settings do HeidiSQL só como dados (passwords desofuscadas vão para o cofre) | `SqlFileTests`, `SplitterFuzzTests`, `CsvFuzzTests`, `HeidiPreferencesImportTests` |
| Exports | Nome ou valor do servidor que vira HTML ativo, código PHP ou comando LaTeX | Escaping por formato (atributos, nomes de variáveis, os dez caracteres especiais do LaTeX) | `GridExporterTests` |
| Segredos | Password em JSON, logs, erros, histórico ou URL | Só no cofre (`ResilientSecretStore`: sem cofre, só em memória); `SecretMasking` no SQL log, no log de queries e no de diagnóstico; histórico sem instruções com passwords | `SecretsInLogsTests`, `SecretMaskingTests` |
| Crash reports e diagnóstico | SQL, valores, hosts ou utilizadores fora da máquina | Crash reports só com tipo, stack, versão, sistema e memória (sem mensagens); log de diagnóstico com redação; nada é enviado | `Infra.Observability.Tests` |
| Ficheiros locais | Outro utilizador lê sessões, histórico ou spill; ficheiro danificado apaga sessões | Pastas 0700 e ficheiros 0600 em Unix; escrita atómica com `.bak`; spill com `DeleteOnClose` e limpeza dos órfãos; `app.db` só com consultas parametrizadas | `Infra.Settings.Tests` |
| Abertura de URLs e pastas | Valor do grid que abre um programa ou um esquema perigoso | Open URL só `http`/`https` com o URI normalizado; Explore folder com o caminho entre aspas | `HeidiGapsTests` |

## 5. Riscos residuais

Os riscos aceites estão em `docs/auditoria-seguranca.md` (R1–R6: TLS "Preferred" sem verificação com o SSL desligado, `AllowPublicKeyRetrieval`, CSV para Excel sem proteção contra fórmulas, journal das query tabs em claro, password na linha de comandos, cleartext authentication). Acrescentam-se:

| # | O quê | Porquê fica | O que reduz o risco |
| --- | --- | --- | --- |
| M1 | O estado da transação segue as instruções enviadas, não o protocolo: um `COMMIT` dentro de uma rotina não se vê | O MySqlConnector não expõe o `SERVER_STATUS_IN_TRANS` (roadmap 1.9) | Só se pergunta quando houve alterações; a status bar mostra a transação |
| M2 | Um ficheiro de settings do HeidiSQL pode trazer sessões para hosts de terceiros ou um startup script | O import é pedido pelo utilizador e as sessões só ligam quando ele as abre | O Session Manager mostra o host antes de ligar |
| M3 | Modo portable e Linux sem keyring não guardam passwords (ficam em memória até fechar) | O cofre por ficheiro com passphrase ainda não existe (roadmap 4.2) | As sessões pedem a password no login |
| M5 | O Image preview descodifica imagens vindas do servidor (Skia, pelo Avalonia) | É o que o HeidiSQL faz; só com o preview ligado, só JPEG/GIF/BMP/PNG reconhecidos pelos primeiros bytes, até 16 MiB, fora da UI thread | Manter o Avalonia e o Skia atualizados (roadmap 2.6) |
| M4 | Sem assinatura do código nem verificação de atualizações | Precisa de certificados (roadmap 1.3 e 1.5) | Descarregar só das releases do repositório e comparar o SHA-256 |

## 6. Quando rever

Rever este documento no mesmo PR quando mudar: opções de ligação do driver (`MySqlConnectionHandle`, `MySqlProvider`), `Infra.Ssh`, `Infra.Secrets.*`, `CsvImportJob`/`BulkLoadAsync`, `SqlFileJob`, o import do HeidiSQL, os exporters, `CrashReports`/`JsonLinesDiagnosticLog`, `SecretMasking`, `ReadOnlyPolicy`, `SafeDdl`, `RowWriter` ou os composers de DML/DDL; ou quando entrar uma funcionalidade que fale com um sistema novo (atualizações, envio de crash reports, outro motor de base de dados).
