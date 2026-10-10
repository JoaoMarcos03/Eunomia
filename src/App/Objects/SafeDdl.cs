using System.Globalization;
using Eunomia.Drivers.Abstractions;
using Eunomia.Engine.Execution;

namespace Eunomia.App.Objects;

/// <summary>
/// DDL da aplicação (table editor, views e rotinas, Drop, Empty; Fase 14) na ligação primária: sem o timeout das queries
/// (um ALTER numa tabela grande demora mais de 30 s e um cancel a meio só faz o servidor desfazer o trabalho), mas com
/// <c>lock_wait_timeout</c> de <see cref="LockWaitSeconds"/> s: um ALTER ou DROP à espera do lock de metadata de outra
/// transação bloqueia todas as queries seguintes a essa tabela (uma paragem de produção). Assim falha depressa, com
/// uma mensagem que diz porquê, e os valores da sessão voltam sempre ao que eram.
/// </summary>
public static class SafeDdl
{
    public const int LockWaitSeconds = 10;

    public static Task<T> RunAsync<T>(SessionHost host, bool strict, Func<IDbConnectionHandle, CancellationToken, Task<T>> work, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(work);
        var dialect = host.ServerServices?.Dialect ?? throw new InvalidOperationException("The session is not connected.");
        return host.RunPrimaryAsync(async (h, ct) =>
        {
            string? mode = null;
            if (strict && dialect.SessionModeQuery is { } query)
            {
                mode = Convert.ToString(await h.ExecuteScalarAsync(query, ct).ConfigureAwait(false), CultureInfo.InvariantCulture) ?? string.Empty;
            }
            var session = dialect.DdlSession(mode, LockWaitSeconds);
            if (session is { } enter)
            {
                await h.ExecuteNonQueryAsync(enter.Enter, ct).ConfigureAwait(false);
            }
            try
            {
                return await work(h, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (host.StatementError(ex) is { Code: 1205 } error)
            {
                throw new DbOperationException(error with
                {
                    Message = error.Message + string.Create(CultureInfo.InvariantCulture,
                        $" The object is in use by another session (an open transaction or a long query holds its metadata lock). Eunomia waits at most {LockWaitSeconds} s so that it does not block the other queries; try again when it is free."),
                });
            }
            finally
            {
                if (session is { } restore && h.IsOpen)
                {
                    await h.ExecuteNonQueryAsync(restore.Restore, CancellationToken.None).ConfigureAwait(false);
                }
            }
        }, cancellationToken, applyQueryTimeout: false);
    }
}
