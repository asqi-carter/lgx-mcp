using Task = System.Threading.Tasks.Task;
#if LOGIX_SDK
using System.Reflection;
using RockwellAutomation.LogixDesigner;
#endif

namespace LogixMcp.Tests;

// LgxSrv_E_FATAL_ERROR / LgxSrv_E_SERVER_FAULTED map to
// SdkServerFaulted, a fatal marks the session dead, and close / reconnect dispose the faulted SDK process.
// The core-only tests run in every build; the session-level tests need the SDK adapter (LOGIX_SDK).

/// <summary>Core (no Rockwell types) tests for the fault mapping, the doctor, close and connect.</summary>
[Collection("StaticHandlerHooks")]
public class SdkServerFaultedCoreTests
{
    private static readonly string FixturePath =
        Path.Combine(AppContext.BaseDirectory, "fixtures", "Demo.L5X");

    [Theory]
    [InlineData("LgxSrv_E_FATAL_ERROR")]
    [InlineData("LgxSrv_E_SERVER_FAULTED")]
    public void FaultCodes_MapToSdkServerFaulted_WithReconnectAndOperationAwareHint(string code)
    {
        var hint = RockwellErrorHints.Find($"Operation failed: {code} (0x80004005)");

        Assert.NotNull(hint);
        Assert.Equal(nameof(TagServiceErrorKind.SdkServerFaulted), hint!.Kind);
        Assert.Contains("logix_connect", hint.NextStep, StringComparison.Ordinal);
        // an earlier change G45: no unconditional "controller was not changed by the failed build"; the claim depends on the operation.
        Assert.DoesNotContain("controller was not changed", hint.NextStep, StringComparison.Ordinal);
        Assert.Contains("depends on the operation", hint.NextStep, StringComparison.Ordinal);

        // The code is found anywhere in an exception chain, not only in the outer message.
        var wrapped = new InvalidOperationException("outer", new Exception($"inner {code}"));
        Assert.True(RockwellErrorHints.IsServerFaulted(wrapped));
        Assert.Equal(nameof(TagServiceErrorKind.SdkServerFaulted), RockwellErrorHints.Find(wrapped)!.Kind);
    }

    [Fact]
    public void FaultCodes_DoNotMatchUnrelatedErrors()
    {
        Assert.False(RockwellErrorHints.IsServerFaulted(new Exception("RxE_COMM_TIMEOUT")));
        Assert.False(RockwellErrorHints.IsServerFaulted(new Exception("Project is not open.")));
    }

    [Fact]
    public async Task Doctor_FaultedSession_ReportsSessionFaulted_AndDoesNotReadPendingZones()
    {
        var env = new DoctorHandlerTests.FakeEnv
        {
            Session = new DoctorSessionInfo("sdk", "online-controller", true, "correlated",
                TimeSpan.FromSeconds(3), Faulted: true),
        };

        var r = await DoctorHandler.ExecuteAsync(null, null, env,
            new Capabilities(LiveWritesEnabled: false, StudioAutomationEnabled: false));

        var session = r.Checks.Single(c => c.Id == "session");
        Assert.Equal(DoctorStatus.Fail, session.Status);
        Assert.StartsWith("session: faulted", session.Observed, StringComparison.Ordinal);
        Assert.Equal(ErrorKinds.SdkServerFaulted, session.Kind);
        Assert.Contains("logix_connect", session.Fix, StringComparison.Ordinal);

        var zone = r.Checks.Single(c => c.Id == "pendingZone");
        Assert.Equal(DoctorStatus.Skipped, zone.Status);
        Assert.Contains("session: faulted", zone.Observed, StringComparison.Ordinal);
        Assert.Equal(0, env.ZoneReads);
    }

    [Fact]
    public void CloseProject_AfterFault_DisposesFaultedSession_EvenWithStagedWork()
    {
        var projects = new ProjectService();
        projects.Load(FixturePath);
        var tags = new TagSessionService();
        var savedInspector = CloseProjectHandler.SdkSessionInspector;
        var savedDisposer  = CloseProjectHandler.SdkSessionDisposer;
        try
        {
            var disposed = 0;
            // Staged work would normally block the close; a dead server cannot publish it.
            CloseProjectHandler.SdkSessionInspector = _ => new SdkSessionCloseInfo(2, true, Faulted: true);
            CloseProjectHandler.SdkSessionDisposer  = _ => disposed++;

            var result = Assert.IsType<CloseProjectResult>(CloseProjectHandler.Execute(projects, tags));

            Assert.True(result.SdkSessionDisposed);
            Assert.Equal(1, disposed);
            Assert.False(projects.IsLoaded);
        }
        finally
        {
            CloseProjectHandler.SdkSessionInspector = savedInspector;
            CloseProjectHandler.SdkSessionDisposer  = savedDisposer;
        }
    }

    [Fact]
    public void CloseProject_AfterFault_WithNoProjectFile_StillDisposesAndSucceeds()
    {
        var savedInspector = CloseProjectHandler.SdkSessionInspector;
        var savedDisposer  = CloseProjectHandler.SdkSessionDisposer;
        try
        {
            var disposed = 0;
            CloseProjectHandler.SdkSessionInspector = _ => new SdkSessionCloseInfo(0, false, Faulted: true);
            CloseProjectHandler.SdkSessionDisposer  = _ => disposed++;

            var result = CloseProjectHandler.Execute(new ProjectService(), new TagSessionService());

            Assert.IsType<CloseProjectResult>(result);
            Assert.Equal(1, disposed);
        }
        finally
        {
            CloseProjectHandler.SdkSessionInspector = savedInspector;
            CloseProjectHandler.SdkSessionDisposer  = savedDisposer;
        }
    }

    [Fact]
    public async Task Connect_AfterFault_DisposesFaultedSession_ThenConnects()
    {
        var projects = new ProjectService();
        projects.Load(FixturePath);
        var tags = new TagSessionService();
        var savedInspector = CloseProjectHandler.SdkSessionInspector;
        var savedDisposer  = CloseProjectHandler.SdkSessionDisposer;
        try
        {
            var disposed = 0;
            CloseProjectHandler.SdkSessionInspector = _ =>
                disposed == 0 ? new SdkSessionCloseInfo(0, false, Faulted: true) : null;
            CloseProjectHandler.SdkSessionDisposer = _ => disposed++;

            var result = await ConnectHandler.ExecuteAsync(
                "198.51.100.1", "1,0", useVirtual: true, projects, tags);

            Assert.IsType<ConnectResult>(result);
            Assert.Equal(1, disposed);
        }
        finally
        {
            CloseProjectHandler.SdkSessionInspector = savedInspector;
            CloseProjectHandler.SdkSessionDisposer  = savedDisposer;
        }
    }

    // #37: a healthy outgoing SDK session is closed before the connect too. Left open, LdSdkServer's
    // keep-alive reaper disposes it later and crashes the service deleting its temp .acd.
    [Fact]
    public async Task Connect_WithHealthySession_DisposesItBeforeConnecting()
    {
        var projects = new ProjectService();
        projects.Load(FixturePath);
        var savedInspector = CloseProjectHandler.SdkSessionInspector;
        var savedDisposer  = CloseProjectHandler.SdkSessionDisposer;
        try
        {
            var disposed = 0;
            CloseProjectHandler.SdkSessionInspector = _ =>
                disposed == 0 ? new SdkSessionCloseInfo(0, false) : null;
            CloseProjectHandler.SdkSessionDisposer  = _ => disposed++;

            var result = await ConnectHandler.ExecuteAsync(
                "198.51.100.1", "1,0", useVirtual: true, projects, new TagSessionService());

            Assert.IsType<ConnectResult>(result);
            Assert.Equal(1, disposed);
        }
        finally
        {
            CloseProjectHandler.SdkSessionInspector = savedInspector;
            CloseProjectHandler.SdkSessionDisposer  = savedDisposer;
        }
    }

    [Fact]
    public async Task Connect_WithNoSdkSession_DisposesNothing()
    {
        var projects = new ProjectService();
        projects.Load(FixturePath);
        var savedInspector = CloseProjectHandler.SdkSessionInspector;
        var savedDisposer  = CloseProjectHandler.SdkSessionDisposer;
        try
        {
            var disposed = 0;
            CloseProjectHandler.SdkSessionInspector = _ => null;
            CloseProjectHandler.SdkSessionDisposer  = _ => disposed++;

            await ConnectHandler.ExecuteAsync(
                "198.51.100.1", "1,0", useVirtual: true, projects, new TagSessionService());

            Assert.Equal(0, disposed);
        }
        finally
        {
            CloseProjectHandler.SdkSessionInspector = savedInspector;
            CloseProjectHandler.SdkSessionDisposer  = savedDisposer;
        }
    }

    [Fact]
    public async Task Connect_WhenDisposerThrows_StillConnects()
    {
        var projects = new ProjectService();
        projects.Load(FixturePath);
        var savedInspector = CloseProjectHandler.SdkSessionInspector;
        var savedDisposer  = CloseProjectHandler.SdkSessionDisposer;
        try
        {
            CloseProjectHandler.SdkSessionInspector = _ => new SdkSessionCloseInfo(0, false);
            CloseProjectHandler.SdkSessionDisposer  = _ => throw new InvalidOperationException("boom");

            var result = await ConnectHandler.ExecuteAsync(
                "198.51.100.1", "1,0", useVirtual: true, projects, new TagSessionService());

            Assert.IsType<ConnectResult>(result);
        }
        finally
        {
            CloseProjectHandler.SdkSessionInspector = savedInspector;
            CloseProjectHandler.SdkSessionDisposer  = savedDisposer;
        }
    }

    // #37: the host disposes the TagSessionService singleton at shutdown; that closes the SDK session once.
    [Fact]
    public void TagSessionService_Dispose_ClosesSdkSessionOnce_AndNeverThrows()
    {
        var savedDisposer = CloseProjectHandler.SdkSessionDisposer;
        try
        {
            var disposed = 0;
            CloseProjectHandler.SdkSessionDisposer = _ => disposed++;
            var tags = new TagSessionService();

            tags.Dispose();
            tags.Dispose();
            Assert.Equal(1, disposed);

            CloseProjectHandler.SdkSessionDisposer = _ => throw new InvalidOperationException("boom");
            new TagSessionService().Dispose();   // must not throw
        }
        finally
        {
            CloseProjectHandler.SdkSessionDisposer = savedDisposer;
        }
    }

    [Fact]
    public void TagSessionService_IsDisposedByTheHostContainer()
    {
        var savedDisposer = CloseProjectHandler.SdkSessionDisposer;
        try
        {
            var disposed = 0;
            CloseProjectHandler.SdkSessionDisposer = _ => disposed++;
            var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
            Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions
                .AddSingleton<TagSessionService>(services);
            using (var provider = Microsoft.Extensions.DependencyInjection.ServiceCollectionContainerBuilderExtensions
                       .BuildServiceProvider(services))
            {
                Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions
                    .GetRequiredService<TagSessionService>(provider);
            }
            Assert.Equal(1, disposed);
        }
        finally
        {
            CloseProjectHandler.SdkSessionDisposer = savedDisposer;
        }
    }
}

#if LOGIX_SDK
/// <summary>
/// Session-level tests: a recording <see cref="ILogixProject"/> proxy throws the SDK fatal, then every
/// later call must fail at once with SdkServerFaulted and never reach the proxy.
/// </summary>
public class SdkServerFaultedSessionTests
{
    private const string EmptyL5X =
        "<RSLogix5000Content><Controller Name=\"C\"/></RSLogix5000Content>";

    /// <summary>
    /// <see cref="ILogixProject"/> stand-in built on <see cref="System.Reflection.DispatchProxy"/>: counts every
    /// SDK call, throws the configured exception while <see cref="FatalText"/> is set, and counts disposals.
    /// </summary>
    public class SdkProxy : System.Reflection.DispatchProxy
    {
        /// <summary>Number of SDK calls made (disposals excluded).</summary>
        public int Calls;
        /// <summary>Number of times the handle was disposed.</summary>
        public int Disposals;
        /// <summary>When set, every SDK call throws an SDK exception carrying this text.</summary>
        public string? FatalText;
        /// <summary>When true, disposing the handle throws (a dead server can fail teardown).</summary>
        public bool DisposeThrows;

        /// <inheritdoc />
        protected override object? Invoke(System.Reflection.MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod!.Name == nameof(IDisposable.Dispose))
            {
                Disposals++;
                if (DisposeThrows) throw new InvalidOperationException("teardown of a faulted server failed");
                return null;
            }

            Calls++;
            if (FatalText is not null)
                throw new OperationFailedException(FatalText, targetMethod.Name);

            var rt = targetMethod.ReturnType;
            if (rt == typeof(void)) return null;
            if (rt == typeof(Task)) return Task.CompletedTask;
            if (rt.IsGenericType && rt.GetGenericTypeDefinition() == typeof(Task<>))
            {
                var inner = rt.GetGenericArguments()[0];
                var value = inner.IsValueType ? Activator.CreateInstance(inner) : null;
                return typeof(Task).GetMethod(nameof(Task.FromResult))!
                    .MakeGenericMethod(inner).Invoke(null, new[] { value });
            }
            return rt.IsValueType ? Activator.CreateInstance(rt) : null;
        }
    }

    private static async Task<(LogixProjectSession Session, SdkProxy Sdk)> OpenAsync()
    {
        var sdk = DispatchProxy.Create<ILogixProject, SdkProxy>();
        var proxy = (SdkProxy)(object)sdk;
        var session = new LogixProjectSession((_, _) => Task.FromResult(sdk));
        await session.OpenAsync("dummy.ACD");
        return (session, proxy);
    }

    /// <summary>Opens a session and triggers the fatal through a real handler call.</summary>
    private static async Task<(LogixProjectSession Session, SdkProxy Sdk)> FaultedAsync(string code)
    {
        var (session, sdk) = await OpenAsync();
        sdk.FatalText = $"The operation failed: {code}";
        var first = await PendingEditsHandler.ExecuteAsync(null, null, session);
        Assert.IsType<PendingEditsError>(first);
        Assert.True(session.IsFaulted);
        return (session, sdk);
    }

    [Theory]
    [InlineData("LgxSrv_E_FATAL_ERROR")]
    [InlineData("LgxSrv_E_SERVER_FAULTED")]
    public async Task Session_FatalCode_MapsToSdkServerFaulted_WithReconnectHint(string code)
    {
        var (session, sdk) = await OpenAsync();
        sdk.FatalText = code;

        var ex = await Assert.ThrowsAsync<TagServiceException>(() =>
            session.ExecuteAsync(p => p.ReadConnectedStateAsync()));

        Assert.Equal(TagServiceErrorKind.SdkServerFaulted, ex.Error.Kind);
        Assert.Equal(code, ex.Error.SdkCode);
        Assert.Contains("logix_connect", ex.Error.Hint, StringComparison.Ordinal);
        Assert.DoesNotContain("controller was not changed", ex.Error.Hint, StringComparison.Ordinal);
        Assert.True(session.IsFaulted);
    }

    [Fact]
    public async Task AfterFatal_LaterSessionCalls_ReturnSdkServerFaulted_WithoutReachingSdk()
    {
        var (session, sdk) = await FaultedAsync("LgxSrv_E_FATAL_ERROR");
        var callsAfterFatal = sdk.Calls;
        sdk.FatalText = null;   // even a healthy SDK must not be reached

        var viaExecute = await Assert.ThrowsAsync<TagServiceException>(() =>
            session.ExecuteAsync(p => p.ReadControllerModeAsync()));
        Assert.Equal(TagServiceErrorKind.SdkServerFaulted, viaExecute.Error.Kind);

        var viaVoidExecute = await Assert.ThrowsAsync<TagServiceException>(() =>
            session.ExecuteAsync(p => p.SaveAsync()));
        Assert.Equal(TagServiceErrorKind.SdkServerFaulted, viaVoidExecute.Error.Kind);

        var viaGetProject = Assert.Throws<TagServiceException>(() => session.GetProject());
        Assert.Equal(TagServiceErrorKind.SdkServerFaulted, viaGetProject.Error.Kind);

        Assert.Equal(callsAfterFatal, sdk.Calls);
    }

    [Fact]
    public async Task AfterFatal_Handlers_ReturnSdkServerFaulted_WithoutReachingSdk()
    {
        var (session, sdk) = await FaultedAsync("LgxSrv_E_SERVER_FAULTED");
        var callsAfterFatal = sdk.Calls;
        sdk.FatalText = null;

        var build = Assert.IsType<BuildError>(await BuildHandler.ExecuteAsync(null, session));
        Assert.Equal(ErrorKinds.SdkServerFaulted, build.Kind);
        Assert.Equal(ErrorKinds.SessionFaulted, build.Session);
        Assert.Contains("logix_connect", build.Hint, StringComparison.Ordinal);

        var export = Assert.IsType<ExportRungsError>(await ExportRungsHandler.ExecuteAsync(
            "Controller/Programs/Program[@Name='MainProgram']/Routines/Routine[@Name='MainRoutine']", session));
        Assert.Equal(ErrorKinds.SdkServerFaulted, export.Kind);
        Assert.Equal(ErrorKinds.SessionFaulted, export.Session);

        var propose = Assert.IsType<ProposeRungEditError>(await ProposeRungEditHandler.ExecuteAsync(
            "Controller/Programs/Program[@Name='MainProgram']/Routines/Routine[@Name='MainRoutine']",
            0, 0, "<Rung Number=\"0\" Type=\"N\"><Text><![CDATA[NOP();]]></Text></Rung>", session));
        Assert.Equal(ErrorKinds.SdkServerFaulted, propose.Kind);
        Assert.Equal(ErrorKinds.SessionFaulted, propose.Session);

        Assert.Equal(callsAfterFatal, sdk.Calls);
    }

    [Fact]
    public async Task PendingEdits_AfterFatal_ReportsSessionFaulted()
    {
        // The call that meets the fatal already reports it, and so does every call after it.
        var (session, sdk) = await OpenAsync();
        sdk.FatalText = "LgxSrv_E_FATAL_ERROR";
        var duringFatal = Assert.IsType<PendingEditsError>(await PendingEditsHandler.ExecuteAsync(null, null, session));
        Assert.Equal(ErrorKinds.SessionFaulted, duringFatal.Session);
        Assert.Equal(ErrorKinds.SdkServerFaulted, duringFatal.Kind);

        var callsAfterFatal = sdk.Calls;
        var later = Assert.IsType<PendingEditsError>(await PendingEditsHandler.ExecuteAsync("MainProgram", null, session));
        Assert.Equal(ErrorKinds.SessionFaulted, later.Session);
        Assert.Equal(ErrorKinds.SdkServerFaulted, later.Kind);
        Assert.Contains("logix_connect", later.Hint, StringComparison.Ordinal);
        Assert.Equal(callsAfterFatal, sdk.Calls);

        var json = System.Text.Json.JsonSerializer.Serialize(later);
        Assert.Contains("\"session\":\"faulted\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Doctor_FaultedSdkSession_ReportsSessionFaulted()
    {
        var (session, sdk) = await FaultedAsync("LgxSrv_E_FATAL_ERROR");
        var tags = new TagSessionService();
        tags.Attach(new TypedSdkTagService(session, L5Sharp.Core.L5X.Parse(EmptyL5X),
            LogixProject.OperationMode.Online), isVirtual: false, isLiveController: true);
        var callsAfterFatal = sdk.Calls;

        var info = new DoctorSdkObservations().GetSession(tags);

        Assert.NotNull(info);
        Assert.True(info!.Faulted);
        Assert.Equal(callsAfterFatal, sdk.Calls);
    }

    [Fact]
    public async Task CloseProjectAndConnect_AfterFault_Succeed_AndDisposeTheFaultedSdkProcess()
    {
        var (session, sdk) = await FaultedAsync("LgxSrv_E_FATAL_ERROR");
        sdk.DisposeThrows = true;   // a dead server may fail teardown; close must still succeed
        var tags = new TagSessionService();
        tags.Attach(new TypedSdkTagService(session, L5Sharp.Core.L5X.Parse(EmptyL5X),
            LogixProject.OperationMode.Online), isVirtual: false, isLiveController: true);

        var savedInspector = CloseProjectHandler.SdkSessionInspector;
        var savedDisposer  = CloseProjectHandler.SdkSessionDisposer;
        try
        {
            CloseProjectHandler.SdkSessionInspector = CloseProjectSdkBridge.Inspect;
            CloseProjectHandler.SdkSessionDisposer  = CloseProjectSdkBridge.Dispose;

            var close = Assert.IsType<CloseProjectResult>(
                CloseProjectHandler.Execute(new ProjectService(), tags));

            Assert.True(close.SdkSessionDisposed);
            Assert.Equal(1, sdk.Disposals);
            Assert.Null(tags.Service);

            // A fresh connect after the fault works.
            var projects = new ProjectService();
            projects.Load(Path.Combine(AppContext.BaseDirectory, "fixtures", "Demo.L5X"));
            var connect = await ConnectHandler.ExecuteAsync(
                "198.51.100.1", "1,0", useVirtual: true, projects, tags);
            Assert.IsType<ConnectResult>(connect);
        }
        finally
        {
            CloseProjectHandler.SdkSessionInspector = savedInspector;
            CloseProjectHandler.SdkSessionDisposer  = savedDisposer;
        }
    }

    [Fact]
    public async Task Connect_AfterFault_DisposesFaultedSdkProcess_BeforeConnecting()
    {
        var (session, sdk) = await FaultedAsync("LgxSrv_E_SERVER_FAULTED");
        var tags = new TagSessionService();
        tags.Attach(new TypedSdkTagService(session, L5Sharp.Core.L5X.Parse(EmptyL5X),
            LogixProject.OperationMode.Online), isVirtual: false, isLiveController: true);

        var savedInspector = CloseProjectHandler.SdkSessionInspector;
        var savedDisposer  = CloseProjectHandler.SdkSessionDisposer;
        try
        {
            CloseProjectHandler.SdkSessionInspector = CloseProjectSdkBridge.Inspect;
            CloseProjectHandler.SdkSessionDisposer  = CloseProjectSdkBridge.Dispose;
            var projects = new ProjectService();
            projects.Load(Path.Combine(AppContext.BaseDirectory, "fixtures", "Demo.L5X"));

            var connect = await ConnectHandler.ExecuteAsync(
                "198.51.100.1", "1,0", useVirtual: true, projects, tags);

            Assert.IsType<ConnectResult>(connect);
            Assert.Equal(1, sdk.Disposals);
            Assert.False(LogixSdkTools.GetActiveSession(tags) is { IsFaulted: true });
        }
        finally
        {
            CloseProjectHandler.SdkSessionInspector = savedInspector;
            CloseProjectHandler.SdkSessionDisposer  = savedDisposer;
        }
    }
}
#endif
