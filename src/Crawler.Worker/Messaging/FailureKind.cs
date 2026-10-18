using System.Net.Sockets;
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
}
