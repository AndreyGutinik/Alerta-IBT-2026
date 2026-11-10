using System.Globalization;

namespace AlertaIBT.Core.Diagnostics;

/// <summary>One event of the Windows event log that matters to how the previous run ended.</summary>
/// <param name="At">When it happened (UTC).</param>
/// <param name="Log">The log it came from (<c>System</c> or <c>Application</c>).</param>
/// <param name="Provider">The provider (<c>Microsoft-Windows-Kernel-Power</c>, <c>User32</c>, <c>EventLog</c>, <c>Application Error</c>...).</param>
/// <param name="Id">The event id (41, 6008, 1074, 42, 107, 1000, 1026...).</param>
/// <param name="Detail">A short, language-neutral detail for the log (who asked for the shutdown, the faulting module), or empty.</param>
public sealed record WindowsEvent(DateTimeOffset At, string Log, string Provider, int Id, string Detail);

/// <summary>What most likely ended the previous run.</summary>
public enum RunEndKind
{
    /// <summary>It ended on its own terms (nothing to say).</summary>
    Clean,

    /// <summary>Windows ended the session (shutdown, restart, logoff) and the app was told.</summary>
    WindowsEndedSession,

    /// <summary>The PC lost power or was forced off (Kernel-Power 41, EventLog 6008).</summary>
    PowerLoss,

    /// <summary>Windows shut down or restarted the PC (event 1074) but the app never heard of it.</summary>
    WindowsShutdown,

    /// <summary>The PC went to sleep and the app never saw it wake up.</summary>
    SleptAndNeverWoke,

    /// <summary>An unhandled exception killed the app (recorded by the app itself).</summary>
    Crash,

    /// <summary>Windows recorded a crash of the app (Application Error, .NET Runtime).</summary>
    NativeCrash,

    /// <summary>The app was closing, froze, and was ended by force by its own watchdog.</summary>
    StuckExit,

    /// <summary>The app ended while Windows stayed up and nothing says why (Task Manager, installer, a crash that left no record).</summary>
    AppGone,
}

/// <summary>The sentence for the log and how serious it is.</summary>
/// <param name="Kind">What most likely happened.</param>
/// <param name="Message">The sentence, in Portuguese; empty for <see cref="RunEndKind.Clean"/>.</param>
/// <param name="IsWarning">Whether it is worth a warning (a normal Windows shutdown is information only).</param>
public sealed record RunEndReport(RunEndKind Kind, string Message, bool IsWarning);

/// <summary>
/// Says, as far as Windows allows without admin rights, what ended the previous run. The inputs are what the run itself left in <c>session.txt</c> (last heartbeat,
/// power events, "Windows asked me to end", a crash record) and the events the Windows log has for the time since the run started (read-only, by
/// <c>WindowsEventLog</c> in the app). No guess is dressed as a fact: the sentence names the event it found, or says it found none.
/// </summary>
public static class RunEndDiagnosis
{
    /// <summary>How much older than the last sign of life an event may be and still count.</summary>
    private static readonly TimeSpan Slack = TimeSpan.FromMinutes(2);

    /// <summary>The report for <paramref name="previous"/>.</summary>
    /// <param name="previous">What the marker said.</param>
    /// <param name="events">The Windows events since the previous run started (empty when none, or when the log could not be read).</param>
    /// <param name="eventsAvailable">Whether the Windows log could be read at all (false: the sentence says so).</param>
    public static RunEndReport Describe(PreviousRun previous, IReadOnlyList<WindowsEvent> events, bool eventsAvailable)
    {
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(events);

        var head = Head(previous);
        var lastSign = previous.LastSignOfLife;

        if (previous.CrashType is { } crash)
        {
            return new RunEndReport(
                RunEndKind.Crash,
                $"{head} caiu com um erro n\U000000E3o tratado ({crash}{(previous.CrashedAt is { } at ? " \U000000E0s " + Clock(at) : string.Empty)}): o detalhe est\U000000E1 no log do dia em que caiu",
                true);
        }

        if (previous.EndedCleanly)
        {
            if (previous.EndReason is { } reason && reason.StartsWith("encerramento travado", StringComparison.Ordinal))
            {
                return new RunEndReport(
                    RunEndKind.StuckExit,
                    $"{head} travou ao encerrar e foi encerrada \U000000E0 for\U000000E7a pelo pr\U000000F3prio app ({reason}); o log do dia diz em que passo",
                    true);
            }

            if (previous.State == "session-ending")
            {
                var shutdown = Find(events, "User32", 1074);
                return new RunEndReport(
                    RunEndKind.WindowsEndedSession,
                    $"{head} terminou porque o Windows encerrou a sess\U000000E3o ({previous.EndReason ?? "desligar ou reiniciar"}{(shutdown is null ? string.Empty : "; evento 1074 \U000000E0s " + Clock(shutdown.At) + Detail(shutdown))}). Normal; o app s\U000000F3 n\U000000E3o teve tempo de gravar o fim",
                    false);
            }

            return new RunEndReport(RunEndKind.Clean, string.Empty, false);
        }

        var since = (lastSign ?? previous.StartedAt ?? DateTimeOffset.MinValue) - Slack;
        var window = events.Where(e => e.At >= since).OrderBy(e => e.At).ToList();
        var last = lastSign is { } signal ? $"\U000000FAltimo sinal do app \U000000E0s {Clock(signal)}" : "sem \U000000FAltimo sinal registrado";

        var loss = Find(window, "Microsoft-Windows-Kernel-Power", 41) ?? Find(window, "EventLog", 6008);
        if (loss is not null)
        {
            return new RunEndReport(
                RunEndKind.PowerLoss,
                $"{head} terminou por queda de energia ou desligamento for\U000000E7ado (evento {loss.Id} do Windows \U000000E0s {Clock(loss.At)}: o PC desligou sem avisar). {last}",
                true);
        }

        if (Find(window, "User32", 1074) is { } planned)
        {
            return new RunEndReport(
                RunEndKind.WindowsShutdown,
                $"{head} terminou porque o Windows desligou ou reiniciou o PC (evento 1074 \U000000E0s {Clock(planned.At)}{Detail(planned)}); o app n\U000000E3o recebeu o aviso. {last}",
                false);
        }

        // Asleep and never woke: the marker saw the PC suspend and never saw it resume, or the Windows log has the last "entering sleep" (42) with no "resume" (107) after it.
        var slept = window.LastOrDefault(e => e.Id == 42 && string.Equals(e.Provider, "Microsoft-Windows-Kernel-Power", StringComparison.OrdinalIgnoreCase));
        var woke = slept is not null && window.Any(e => e.Id == 107 && e.At >= slept.At && string.Equals(e.Provider, "Microsoft-Windows-Kernel-Power", StringComparison.OrdinalIgnoreCase));
        if (previous.State == "suspending" || (slept is not null && !woke))
        {
            var when = slept?.At ?? previous.SuspendedAt;
            return new RunEndReport(
                RunEndKind.SleptAndNeverWoke,
                $"{head} terminou com o PC suspenso e ele n\U000000E3o voltou{(when is { } moment ? " (suspendeu \U000000E0s " + Clock(moment) + ")" : string.Empty)}: o app n\U000000E3o viu o retorno. {last}",
                true);
        }

        if (window.FirstOrDefault(e => e.Log == "Application" && e.Id is 1000 or 1026) is { } appCrash)
        {
            return new RunEndReport(
                RunEndKind.NativeCrash,
                $"{head} caiu: o Windows registrou o evento {appCrash.Id} ({appCrash.Provider}) \U000000E0s {Clock(appCrash.At)}{Detail(appCrash)}. {last}",
                true);
        }

        var tail = eventsAvailable
            ? "o Windows n\U000000E3o registrou desligamento, queda de energia, suspens\U000000E3o nem erro do app nesse per\U000000EDodo"
            : "o log do Windows n\U000000E3o p\U000000F4de ser lido";
        return new RunEndReport(
            RunEndKind.AppGone,
            $"{head} terminou sem aviso com o Windows ligado: {last}; {tail} (Gerenciador de Tarefas, instalador, ou queda do app sem registro)",
            true);
    }

    private static string Head(PreviousRun previous) =>
        $"A execu\U000000E7\U000000E3o anterior (vers\U000000E3o {previous.Version ?? "?"}, iniciada em {(previous.StartedAt is { } at ? at.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) : "?")})";

    private static WindowsEvent? Find(IEnumerable<WindowsEvent> events, string provider, int id) =>
        events.FirstOrDefault(e => e.Id == id && string.Equals(e.Provider, provider, StringComparison.OrdinalIgnoreCase));

    private static string Clock(DateTimeOffset at) => at.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture);

    private static string Detail(WindowsEvent e) => e.Detail.Length == 0 ? string.Empty : "; " + e.Detail;
}
