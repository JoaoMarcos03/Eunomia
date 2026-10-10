# Desempenho em servidores remotos

Como o Eunomia se mantém rápido com servidores lentos ou longe (VPN, outra cidade, túnel SSH), o que se mediu a 9 out 2026 e como voltar a medir. Complementa as medições da secção 10 da spec (PR-xx) e o `bench/README.md`.

## Princípios

1. **A UI thread nunca espera pela rede.** Cada ligação tem a sua fila (`ConnectionLane`) numa thread de fundo; o driver é assíncrono. Medido: com 8 s de rede a chegar, a maior pausa da UI thread foi 56 ms (o critério 27.3 é 250 ms).
2. **Cada ida ao servidor conta.** Num servidor a 50–150 ms, o tempo de uma ação é sobretudo o número de idas em série. As consultas de metadata vão juntas num só pedido (multi-statement) e a cache não repete o que já está a caminho.
3. **O que já não interessa não ocupa ligações.** Escolher outra coisa na árvore cancela a carga do Data tab, a pré-carga da definição e o table editor da escolha anterior; uma query na mesma sessão cancela a carga do Data tab.
4. **Cancelar também custa.** O `KILL QUERY` abre outra ligação; numa query que já acabou, o MySqlConnector esperava o `CancellationTimeout` (2 s) e fechava o socket. Trabalho abandonado ao navegar tem uma margem para acabar sozinho; o Esc cancela logo.
5. **Trabalho pesado não fica à frente do trabalho visível.** A prioridade de uma fila só ordena o que espera; não interrompe o que está a correr. Por isso as estatísticas da árvore vão para uma ligação de job.

## Ligações de uma sessão

| Ligação | O que leva | Cancelar |
| --- | --- | --- |
| Primária | Queries do utilizador, Data tab, edições, `USE` | Esc: logo. Carga do Data tab abandonada: 0,5 s de margem (`TableBrowser.AbandonGrace`) |
| Metadata | Árvore (nomes), definições de tabelas, autocomplete, contagens | Definição abandonada: 3 s de margem (`MetadataCache.CancelGrace`); fica na cache |
| Jobs (2 por omissão) | Export, import, Run SQL file, Find text e as estatísticas da árvore | Pelo diálogo de cada job |

No modo de ligação única ("Separate metadata connection" desligado), metadata e estatísticas ficam na primária, como no HeidiSQL.

## Decisões e o que mudaram

| Mudança | Efeito medido |
| --- | --- |
| Definição de uma tabela (colunas, chaves, FKs, checks, opções) num só pedido em vez de seis | 5 idas a menos por tabela |
| A cache de metadata reaproveita o pedido anterior da mesma tabela na fila (a pré-carga e o Data tab pediam-na duas vezes) | 6 idas a menos por clique |
| Collations e engines do table editor pedidos uma vez por sessão | 2 idas a menos por tabela no Table tab |
| `SHOW TRIGGERS FROM` em vez de `information_schema.TRIGGERS` | 590 ms → 33 ms no MariaDB 10.4 (o `information_schema` percorria as tabelas de todas as bases) |
| Estatísticas da árvore (`SHOW TABLE STATUS`) numa ligação de job | Base de 5.000 tabelas: 50 s a frio no MariaDB 10.4 que prendiam a ligação de metadata; uma tabela aberta logo a seguir passou de 6–50 s para 0,33 s |
| Uptime e hora do servidor num pedido com `SHOW STATUS` | Metade dos pedidos a cada 20 s; passou a funcionar no MySQL 8 (o `information_schema.GLOBAL_STATUS` não existe lá) |
| Margem antes do `KILL QUERY` no trabalho abandonado | Setas por 10 tabelas: 9,5 s → 1,5 s |
| Cancelar a carga do Data tab em qualquer escolha na árvore (antes do `USE`), em Next e Show all, e num Run na mesma sessão | Tabela de outra base a meio de uma carga: 7,1 s → 1,8 s; Show all e outra tabela: mais de 3 min → 1,7 s |

## Números (9 out 2026, servidor de teste MariaDB 10.4 local por um proxy de 50 ms por pedido)

| Ação | Antes | Depois |
| --- | --- | --- |
| Abrir uma base (32 tabelas) | 1,1–1,7 s | 0,26 s |
| Abrir uma tabela no Data tab | 1,2 s, 14 pedidos | 0,53 s, 3 pedidos |
| Outra tabela da mesma base | 0,75 s, 11 pedidos | 0,25 s, 2 pedidos |
| Tabela no Table tab (seguinte) | 1,2 s, 12 pedidos | 0,5 s, 1 pedido |
| Setas por 10 tabelas, 120 ms entre cada | 9,5 s | 1,5 s |

Com 256 KB/s e uma tabela de 1,6 KB por linha (primeira página de 8 s), mudar de tabela a meio da carga leva 1,6–1,8 s (a margem de 0,5 s mais o `KILL QUERY` pela rede lenta). Ler 300.000 linhas × 7 colunas pelo executor fica a 13% do MySqlConnector sozinho (Debug), por isso o caminho até aos blocos não precisa de mais trabalho.

Valores indicativos: esta máquina não é a de referência.

## Como medir

```powershell
powershell -File tests\Integration\Start-TestServer.ps1
$env:EUNOMIA_PERF = "1"
dotnet test tests\UI.Headless --filter "FullyQualifiedName~SlowNetworkTests"
dotnet test tests\UI.Headless --filter "FullyQualifiedName~TabelaLogoDepoisDeAbrirUmaBaseGrande"
dotnet test tests\Integration --filter "FullyQualifiedName~LeituraDeUmResultadoGrande"
```

| Teste | Resultado em `%TEMP%` |
| --- | --- |
| `SlowNetworkTests.IdasAoServidorPorAcao`: tempo e pedidos por ação, com as instruções do SQL log | `eunomia-roundtrips.txt` |
| `SlowNetworkTests.UiFluidaECargaCanceladaComRedeLenta`: pausas da UI thread e mudança de tabela a meio de uma carga a 256 KB/s | `eunomia-slow-network.txt` |
| `PerformanceTests.TabelaLogoDepoisDeAbrirUmaBaseGrande` (cria `eunomia_pr08` com 5.000 tabelas na primeira vez) | `eunomia-table-after-big-db.txt` |
| `QueryPerformanceTests.LeituraDeUmResultadoGrande` | `eunomia-read-throughput.txt` |

O proxy dos testes (`SlowProxy` em `SlowNetworkTests.cs`) atrasa cada pedido do cliente e limita a largura de banda no sentido servidor → cliente.

## Guardas nos testes de sempre

Correm em cada `dotnet test` com o servidor de teste:

- `RoundTripTests`: abrir uma tabela no Data tab são três instruções (`USE`, definição, `SELECT`), outra da mesma base duas, e o table editor só a definição depois da primeira tabela.
- `DataLoadCancelTests`: escolher outra coisa (tabela de outra base, nó da base, query, outra tabela durante o Show all) não espera pelo fim de uma carga lenta.
- `TableBrowserTests.ShowAllCanceladoGuardaAsLinhasENextContinua`.
- `MetadataCacheTests.NomesPrimeiroEstatisticasDepois`: as estatísticas usam uma ligação de job.
