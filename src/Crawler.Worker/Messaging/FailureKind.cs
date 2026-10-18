using System.Net.Sockets;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using RabbitMQ.Client.Exceptions;

namespace Crawler.Worker.Messaging;

/// <summary>
/// Tells "our infrastructure is unreachable" apart from "something went wrong once". The difference decides
/// whether a retry costs one of the message's attempts: a database or broker outage must not burn through
/// the attempt budget and dead-letter healthy pages; the message should simply wait until the outage ends.
/// </summary>
public static class FailureKind
{
    public static bool IsInfrastructureUnavailable(Exception ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
        {
            switch (e)
            {
                case PostgresException { SqlState: "40001" or "40P01" }:
                    return false; // deadlock / serialization failure: retry, but on the attempt budget, so a recurring one is given up
                case NpgsqlException { IsTransient: true }:
                case SocketException:
                case TimeoutException:
                case EndOfStreamException:
                case BrokerUnreachableException:
                case AlreadyClosedException:
                    return true;
                case DbUpdateException:
                    continue; // look at what EF wrapped
            }
        }

        return false;
    }

    /// <summary>A unique-index collision: another worker wrote the same row first. Postgres 23505; SQLite constraint error 19 (tests).</summary>
    public static bool IsUniqueViolation(Exception ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
        {
            switch (e)
            {
                case PostgresException { SqlState: "23505" }:
                case SqliteException { SqliteErrorCode: 19 }:
                    return true;
            }
        }

        return false;
    }
}
