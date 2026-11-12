using System.Net.Sockets;
using Microsoft.AspNetCore.Connections;

namespace PriorAuth.Api;

/// <summary>
/// Turns "could not listen on the port" into what to do about it. The port is either held by another program
/// (often another copy of this service), or, on Windows, reserved by Hyper-V, WSL or Docker, which take blocks of
/// ports and can take different ones after a restart.
/// </summary>
public static class PortProblem
{
    /// <summary>The explanation for a port failure, or null when the failure is something else.</summary>
    public static string? Explain(Exception failure)
    {
        var reserved = false;
        var found = false;
        for (Exception? e = failure; e is not null; e = e.InnerException)
        {
            if (e is AddressInUseException) found = true;
            if (e is SocketException socket)
            {
                if (socket.SocketErrorCode == SocketError.AddressAlreadyInUse) found = true;
                if (socket.SocketErrorCode == SocketError.AccessDenied) found = reserved = true;
            }
        }
        if (!found) return null;

        return $"""
            The service could not start: {failure.Message}
            {(reserved
                ? "Windows refused the port. It is usually reserved by Hyper-V, WSL or Docker."
                : "Another program is using the port, often another copy of this service. On Windows the port may also be reserved by Hyper-V, WSL or Docker.")}

            To fix it, either:
              - stop the other program, or
              - start on another port, and point the clinic demo at it:
                  dotnet run --project src/PriorAuth.Api --no-launch-profile -- --urls http://localhost:5120 --environment Development
                  dotnet run --project samples/PriorAuth.ClinicDemo -- --urls http://localhost:5130 --PlanApi http://localhost:5120

            On Windows, list the reserved ports with:
              netsh interface ipv4 show excludedportrange protocol=tcp
            See "If a port is in use" in docs/03-running-and-testing.md.
            """;
    }
}
