# Auditoria de segurança (9 out 2026)

Revisão do código da 1.0.0-beta.1 contra a secção 23 da spec, a pedido do utilizador depois do feedback da beta. Complementa a auditoria do SQL montado da Fase 14 (passo 0 em `docs/progress.md`), que não se repetiu aqui.

## Método

Leitura do código nos pontos onde dados de fora (servidor, ficheiros, linha de comandos, clipboard) entram ou onde a aplicação escreve para fora (disco, browser, outros programas): ligações e TLS, `LOAD DATA LOCAL`, segredos, logs, histórico, ficheiros temporários, exports, SSH, abertura de URLs e de ficheiros, SQLite local. As afirmações sobre o MySqlConnector 2.6.2 foram confirmadas no próprio pacote, não de memória. `dotnet list package --vulnerable --include-transitive`: nenhum pacote com vulnerabilidades conhecidas.

## Corrigido

| # | Problema | Risco | Correção | Teste |
| --- | --- | --- | --- | --- |
| A1 | Export HTML e XML: nomes de tabela e de coluna (vêm do servidor) dentro de atributos sem escapar a aspa; a query ia para o HTML sem escaping nenhum | Um nome com `"` acrescentava atributos (`onmouseover=…`) a um HTML aberto no browser; uma query com `<` estragava a página | Atributos com `&quot;`; a query com escaping | `GridExporterTests.NomesEValoresHostisNaoSaemDoFormato` |
| A2 | Export PHP: o nome da tabela entrava cru no nome da variável | Um nome de tabela como `x = system('…'); $y` virava código PHP no ficheiro | Nome de variável só com caracteres válidos | idem |
| A3 | Export LaTeX: só `_ $ % &` escapados | Um valor `\input{…}` ou `\write18{…}` corria ao compilar o documento | Os dez caracteres especiais escapados | idem |
| A4 | Histórico de queries guardava `CREATE USER … IDENTIFIED BY '…'` em claro no `app.db` (também o importado do HeidiSQL) | Passwords em disco, ao contrário da regra "só no cofre do sistema" (23.2) | Instruções com passwords não entram no histórico (o SQL log já as mascarava) | `SecretMaskingTests`, `HeidiPreferencesImportTests` |
| A5 | SSH: uma chave de outro tipo num host já conhecido era tratada como primeiro contacto | Um intermediário podia apresentar outro tipo de chave e só aparecia a pergunta normal | Pergunta com aviso explícito, como o OpenSSH | `SshTests` |
| A6 | Não se via se a ligação ia cifrada (a spec pede que uma ligação sem verificação fique visível) | Ligações remotas em claro passavam despercebidas | O driver lê a cifra TLS na abertura; aviso no SQL log para servidores remotos sem TLS nem SSH; "Encryption" no tooltip do servidor na status bar | `SessionHostTests`, `LocalHostTests` |
| A7 | Markdown: `\|` e quebras de linha num valor partiam a tabela | Robustez | Escapados | `GridExporterTests` |

## Verificado e em ordem

- **Segredos:** Credential Manager, Keychain e Secret Service; sem cofre, só em memória até fechar (`ResilientSecretStore`); nunca em JSON. O export de settings não leva segredos. A connection string com a password não é guardada (`builder.Password` limpo depois de criar a ligação).
- **Logs:** SQL log, "Log queries to file" e log de diagnóstico mascaram `IDENTIFIED BY`, `SET PASSWORD`, `PASSWORD()` e connection strings. Os crash reports só têm tipo e stack, sem mensagens.
- **`LOAD DATA LOCAL`:** só na ligação dedicada do import CSV, fechada no fim. O MySqlConnector 2.6.2 só entrega o stream registado pelo `MySqlBulkLoader` (prefixo `:SOURCE:`) e recusa pedidos de ficheiros por nome quando o servidor não é verificado ("Use SourceStream or SslMode >= VerifyCA"): um servidor malicioso não lê ficheiros do PC.
- **SSH:** known_hosts próprio, chave mudada bloqueia a ligação; o túnel só escuta em `127.0.0.1`.
- **Ficheiros:** spill com `DeleteOnClose` e limpeza dos órfãos no arranque; pastas 0700 e ficheiros 0600 em Unix; `.bak` e escrita atómica.
- **SQLite local:** todas as consultas com parâmetros.
- **Open URL:** só `http`/`https`, e passa o `AbsoluteUri` normalizado; "Explore folder" com o caminho entre aspas.
- **SQL montado:** auditado na Fase 14 (literais pelo dialeto, `SafeDdl`, `RowWriter`); as alterações desta auditoria não montam SQL.

## Riscos aceites ou por decidir

| # | O quê | Porquê fica | O que reduz o risco |
| --- | --- | --- | --- |
| R1 | Com o SSL da sessão desligado, o TLS é "Preferred" sem verificar o certificado (decisão provisória da Fase 3; a spec pede verificação por omissão) | Liga como o HeidiSQL a servidores com certificados próprios; verificar por omissão partia a maioria das ligações existentes | O aviso A6 mostra quando não há TLS; para produção, SSL ligado com "Verify CA and hostname" |
| R2 | `AllowPublicKeyRetrieval=true` | Sem isto, contas `caching_sha2_password` do MySQL 8 sem TLS não ligam; com TLS não é usado | Só pesa sem TLS, onde o A6 já avisa |
| R3 | CSV para Excel sem proteção contra fórmulas (`=`, `+`, `-`, `@` no início) | Alterar os valores estragava o round trip (reimportar o CSV) e o HeidiSQL também não protege; o Excel atual bloqueia DDE por omissão | Abrir CSV de origem desconhecida com cuidado |
| R4 | O texto das query tabs (journal de recuperação) fica em disco como foi escrito, passwords incluídas | É o documento do utilizador; mascarar perdia o que escreveu | Pastas do utilizador com permissões próprias |
| R5 | `-p`/`--password` na linha de comandos fica visível na lista de processos | Compatibilidade com os atalhos do HeidiSQL | Preferir sessões gravadas (password no cofre) |
| R6 | "Enable cleartext authentication" não tem efeito | O MySqlConnector trata o plugin sozinho quando o servidor o pede e recusa enviar a password em claro sem TLS com certificado verificado (mensagem "requires a secure connection with a verified server certificate to send the password" no pacote): mais seguro do que o HeidiSQL | Nenhum |
