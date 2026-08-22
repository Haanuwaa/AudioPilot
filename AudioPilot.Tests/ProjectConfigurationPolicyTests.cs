using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace AudioPilot.Tests;

public sealed partial class ProjectConfigurationPolicyTests
{
    private static readonly string[] ConfigurationWindowFiles =
    [
        "MainWindow.xaml",
        "RoutineEditorWindow.xaml",
        "PackagedAppPickerWindow.xaml",
        "Views/SettingsDeviceSwitchingPanel.xaml",
        "Views/SettingsGeneralPanel.xaml",
        "Views/OutputDevicePanel.xaml",
        "Views/InputDevicePanel.xaml",
    ];
    [Fact]
    public void FullTestRun_IsolatesNativeIntegrationAndStressCategories()
    {
        string repositoryRoot = ResolveRepositoryRoot();
        string testRunner = File.ReadAllText(Path.Combine(repositoryRoot, "scripts", "run-tests.ps1"));

        Assert.Contains("$fullCategories = @(\"unit\", \"integration\", \"stress\")", testRunner, StringComparison.Ordinal);
        Assert.Contains("& pwsh -NoProfile -ExecutionPolicy Bypass -EncodedCommand $encodedCommand", testRunner, StringComparison.Ordinal);
        Assert.Contains("if ($LASTEXITCODE -ne 0)", testRunner, StringComparison.Ordinal);
    }

    [Fact]
    public void RuntimeProfiler_UsesBoundedSamplesAndSafeTemporaryOutput()
    {
        string repositoryRoot = ResolveRepositoryRoot();
        string profiler = File.ReadAllText(Path.Combine(repositoryRoot, "scripts", "profile-runtime.ps1"));

        Assert.Contains("[ValidateRange(1, 86400)]", profiler, StringComparison.Ordinal);
        Assert.Contains("[ValidateRange(0.1, 3600)]", profiler, StringComparison.Ordinal);
        Assert.Contains("[System.IO.Path]::GetTempPath()", profiler, StringComparison.Ordinal);
        Assert.Contains("AudioPilotDiagnostics", profiler, StringComparison.Ordinal);
        Assert.Contains("$logicalProcessorCount", profiler, StringComparison.Ordinal);
        Assert.Contains("PrivateMemoryMiB", profiler, StringComparison.Ordinal);
        Assert.Contains("HandleCount", profiler, StringComparison.Ordinal);
        Assert.Contains("ThreadCount", profiler, StringComparison.Ordinal);
        Assert.Contains("[System.IO.File]::Move($temporaryPath, $destinationPath, $true)", profiler, StringComparison.Ordinal);
        Assert.DoesNotContain("Stop-Process", profiler, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MainWindow_DefersHeavyInactiveTabsUntilFirstSelection()
    {
        string repositoryRoot = ResolveRepositoryRoot();
        XDocument mainWindow = XDocument.Load(Path.Combine(repositoryRoot, "AudioPilot", "MainWindow.xaml"));
        XElement[] deferredTemplates =
        [..
            mainWindow
                .Descendants()
                .Where(static element => element.Name.LocalName == "DeferredTabContentBehavior.Template")
        ];

        Assert.Equal(3, deferredTemplates.Length);
        Assert.All(deferredTemplates, template =>
            Assert.Contains(template.Descendants(), static element => element.Name.LocalName == "DataTemplate"));
        XElement inputTab = Assert.Single(
            mainWindow.Descendants(),
            static element => element.Name.LocalName == "TabItem" && GetAttribute(element, "Header") == "Input");
        Assert.Contains(
            inputTab.Descendants(),
            static element => element.Name.LocalName == "InputDevicePanel"
                && GetAttribute(element, "Loaded") == "InputDevicePanel_Loaded");
    }

    [Fact]
    public void ApplicationSources_DoNotRepeatProjectGlobalUsings()
    {
        string repositoryRoot = ResolveRepositoryRoot();
        string applicationRoot = Path.Combine(repositoryRoot, "AudioPilot");
        string globalUsingsPath = Path.Combine(applicationRoot, "GlobalUsings.ServiceDomains.cs");
        HashSet<string> globalNamespaces =
        [..
            File.ReadLines(globalUsingsPath)
                .Select(static line => TryParseUsingNamespace(line, "global using "))
                .Where(static value => value != null)
                .Cast<string>()
        ];

        List<string> redundantUsings = [];
        foreach (string sourcePath in Directory.EnumerateFiles(applicationRoot, "*.cs", SearchOption.AllDirectories))
        {
            string relativePath = Path.GetRelativePath(repositoryRoot, sourcePath).Replace('\\', '/');
            if (string.Equals(sourcePath, globalUsingsPath, StringComparison.OrdinalIgnoreCase)
                || relativePath.Contains("/bin/", StringComparison.OrdinalIgnoreCase)
                || relativePath.Contains("/obj/", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            int lineNumber = 0;
            foreach (string line in File.ReadLines(sourcePath))
            {
                lineNumber++;
                string? namespaceName = TryParseUsingNamespace(line, "using ");
                if (namespaceName != null && globalNamespaces.Contains(namespaceName))
                {
                    redundantUsings.Add($"{relativePath}:{lineNumber} repeats global using {namespaceName}");
                }
            }
        }

        Assert.True(
            redundantUsings.Count == 0,
            $"Source files must rely on GlobalUsings.ServiceDomains.cs instead of repeating its imports:{Environment.NewLine}{string.Join(Environment.NewLine, redundantUsings)}");
    }

    [Fact]
    public void RuntimeHostAsyncCleanup_CompletesExplicitApplicationShutdown()
    {
        string repositoryRoot = ResolveRepositoryRoot();
        string runtimeHost = File.ReadAllText(Path.Combine(repositoryRoot, "AudioPilot", "AppRuntimeHost.cs"));
        string mainWindow = File.ReadAllText(Path.Combine(repositoryRoot, "AudioPilot", "MainWindow.xaml.cs"));

        Assert.Contains("private async Task ShutdownCoreAsync(string reason)", runtimeHost, StringComparison.Ordinal);
        Assert.Contains("await _windowManager.CloseForShutdownAsync();", runtimeHost, StringComparison.Ordinal);
        Assert.Contains("_application.Shutdown();", runtimeHost, StringComparison.Ordinal);
        int shutdownCore = runtimeHost.IndexOf("private async Task ShutdownCoreAsync(string reason)", StringComparison.Ordinal);
        int shutdownBarrier = runtimeHost.IndexOf("TryBeginShutdownAdmissionBarrier(opId)", shutdownCore, StringComparison.Ordinal);
        int windowDrain = runtimeHost.IndexOf("CloseApplicationWindowsAsync()", shutdownBarrier, StringComparison.Ordinal);
        Assert.True(shutdownCore >= 0 && shutdownBarrier > shutdownCore && windowDrain > shutdownBarrier);
        Assert.Contains("TryShutdownAction(_windowManager.BeginShutdown", runtimeHost, StringComparison.Ordinal);
        Assert.Contains("TryShutdownAction(_trayService.BeginShutdown", runtimeHost, StringComparison.Ordinal);
        Assert.Contains("TryShutdownAction(_hotkeyBindings.Unwire", runtimeHost, StringComparison.Ordinal);
        Assert.Contains("runtimeHost?.BeginEmergencyShutdown();", File.ReadAllText(Path.Combine(repositoryRoot, "AudioPilot", "App.xaml.cs")), StringComparison.Ordinal);
        Assert.DoesNotContain("AppRuntimeServiceBundle", mainWindow, StringComparison.Ordinal);
        Assert.DoesNotContain("HotkeyService", mainWindow, StringComparison.Ordinal);
        Assert.DoesNotContain("SystemEvents.", mainWindow, StringComparison.Ordinal);
    }

    [Fact]
    public void ConfiguredStartup_DoesNotEagerlyConstructOrMaterializeMainWindow()
    {
        string repositoryRoot = ResolveRepositoryRoot();
        string appSource = File.ReadAllText(Path.Combine(repositoryRoot, "AudioPilot", "App.xaml.cs"));
        string runtimeHost = File.ReadAllText(Path.Combine(repositoryRoot, "AudioPilot", "AppRuntimeHost.cs"));
        XDocument mainWindow = XDocument.Load(Path.Combine(repositoryRoot, "AudioPilot", "MainWindow.xaml"));

        Assert.DoesNotContain("new MainWindow()", appSource, StringComparison.Ordinal);
        Assert.DoesNotContain("EnsureHandle", appSource, StringComparison.Ordinal);
        Assert.Contains("windowManager.SetWindowFactory(() => new MainWindow(", runtimeHost, StringComparison.Ordinal);
        Assert.DoesNotContain(mainWindow.Descendants(), static element => element.Name.LocalName == "TaskbarIcon");
    }

    [Fact]
    public void FirstPresentation_ThemesAndRendersWindowsBeforeReveal()
    {
        string repositoryRoot = ResolveRepositoryRoot();
        string manager = File.ReadAllText(Path.Combine(repositoryRoot, "AudioPilot", "Services", "UI", "AppMainWindowManager.cs"));
        string dialogHelper = File.ReadAllText(Path.Combine(repositoryRoot, "AudioPilot", "Helpers", "DialogWindowHelper.cs"));
        XDocument mainWindow = XDocument.Load(Path.Combine(repositoryRoot, "AudioPilot", "MainWindow.xaml"));

        Assert.Equal("0", GetAttribute(mainWindow.Root!, "Opacity"));
        Assert.Equal("False", GetAttribute(mainWindow.Root!, "ShowActivated"));
        Assert.Null(mainWindow.Root!.Attribute("Visibility"));
        Assert.Contains("WindowFirstPresentationHelper.Prepare(window);", manager, StringComparison.Ordinal);
        Assert.Contains("WindowFirstPresentationHelper.TryApplyNativeClientBackground(window, ensureHandle: true);", manager, StringComparison.Ordinal);
        Assert.Contains("WindowFirstPresentationHelper.StageOffscreenFirstRender(window);", manager, StringComparison.Ordinal);
        Assert.True(
            manager.IndexOf("WindowFirstPresentationHelper.StageOffscreenFirstRender(window);", StringComparison.Ordinal)
            < manager.IndexOf("WindowFirstPresentationHelper.TryApplyNativeClientBackground(window, ensureHandle: true);", StringComparison.Ordinal),
            "The first window must be moved offscreen before its HWND is created.");
        Assert.True(
            manager.IndexOf("window.ShowInTaskbar = true;", StringComparison.Ordinal)
            < manager.IndexOf("WindowFirstPresentationHelper.TryApplyNativeClientBackground(window, ensureHandle: true);", StringComparison.Ordinal),
            "Taskbar policy must be final before the first HWND is created.");
        Assert.True(
            manager.IndexOf("WindowFirstPresentationHelper.BeginOffscreenFirstRender(window);", StringComparison.Ordinal)
            < manager.IndexOf("WindowFirstPresentationHelper.TryApplyNativeClientBackground(window, ensureHandle: true);", StringComparison.Ordinal),
            "Opacity must be final before the first HWND is created.");
        Assert.Contains("WindowFirstPresentationHelper.BeginOffscreenFirstRender(window)", manager, StringComparison.Ordinal);
        Assert.Contains("WindowFirstPresentationHelper.RevealAsync", manager, StringComparison.Ordinal);
        string presentationHelper = File.ReadAllText(Path.Combine(repositoryRoot, "AudioPilot", "Helpers", "WindowFirstPresentationHelper.cs"));
        Assert.Contains("DispatcherPriority.Render", presentationHelper, StringComparison.Ordinal);
        Assert.DoesNotContain("DWMWA_CLOAK", presentationHelper, StringComparison.Ordinal);
        Assert.Contains("WaitForFirstContentRenderAsync", presentationHelper, StringComparison.Ordinal);
        Assert.DoesNotContain("CompositionTarget.Rendering", presentationHelper, StringComparison.Ordinal);
        Assert.Contains("WindowFirstPresentationHelper.Prepare(dialog, hideFromTaskbar: false);", dialogHelper, StringComparison.Ordinal);
        Assert.Contains("WindowThemeResolver.ApplyOwnerOrMainWindowTheme(dialog);", dialogHelper, StringComparison.Ordinal);
        Assert.Contains("dialog.ContentRendered += contentRenderedHandler;", dialogHelper, StringComparison.Ordinal);
    }

    [Fact]
    public void RuntimeHostStartupFailure_DoesNotPublishAnIncompleteHost()
    {
        string repositoryRoot = ResolveRepositoryRoot();
        string source = File.ReadAllText(Path.Combine(repositoryRoot, "AudioPilot", "App.xaml.cs"));

        Assert.Contains("_runtimeHost = await AppRuntimeHost.CreateAndInitializeAsync(", source, StringComparison.Ordinal);
        Assert.Contains("catch (AppRuntimeStartupAbortedException ex)", source, StringComparison.Ordinal);
        Assert.Contains("Failed to initialize application runtime", source, StringComparison.Ordinal);
        Assert.Contains("ShutdownWithCode(3);", source, StringComparison.Ordinal);
    }

    [Fact]
    public void RoutineScheduleSelectors_RetainImplicitThemeStylesAndLocaleBindings()
    {
        string repositoryRoot = ResolveRepositoryRoot();
        XDocument editor = XDocument.Load(Path.Combine(repositoryRoot, "AudioPilot", "RoutineEditorWindow.xaml"));
        XElement periodSelector = Assert.Single(
            editor.Descendants(),
            element => element.Name.LocalName == "ComboBox"
                && string.Equals(GetAttribute(element, "AutomationProperties.Name"), "Scheduled AM or PM", StringComparison.Ordinal));

        Assert.DoesNotContain(periodSelector.Elements(), static element => element.Name.LocalName == "ComboBox.Style");
        Assert.Equal("{Binding AmPmOptions}", GetAttribute(periodSelector, "ItemsSource"));
        Assert.Contains("InverseBoolToVisibilityConverter", GetAttribute(periodSelector, "Visibility"), StringComparison.Ordinal);

        XElement minuteSelector = Assert.Single(
            editor.Descendants(),
            element => element.Name.LocalName == "ComboBox"
                && string.Equals(GetAttribute(element, "AutomationProperties.Name"), "Scheduled minute", StringComparison.Ordinal));
        Assert.Equal("224", GetAttribute(minuteSelector, "MaxDropDownHeight"));

        XElement timeZoneLabel = Assert.Single(
            editor.Descendants(),
            element => element.Name.LocalName == "TextBlock"
                && string.Equals(GetAttribute(element, "AutomationProperties.Name"), "Scheduled routine time zone", StringComparison.Ordinal));
        Assert.Null(timeZoneLabel.Attribute("ToolTip"));
        XElement hoverBehavior = Assert.Single(
            timeZoneLabel.Descendants(),
            static element => element.Name.LocalName == "HoverInfoPopupBehavior");
        Assert.Equal("{Binding ScheduleTimeZoneDetails}", GetAttribute(hoverBehavior, "Text"));
    }

    [Fact]
    public void ProductionXaml_UsesThemedHoverBehaviorInsteadOfNativeTooltips()
    {
        string repositoryRoot = ResolveRepositoryRoot();
        string[] violations = [.. Directory.GetFiles(
                Path.Combine(repositoryRoot, "AudioPilot"),
                "*.xaml",
                SearchOption.AllDirectories)
            .SelectMany(path => XDocument.Load(path)
                .Descendants()
                .Where(element => element.Attributes().Any(attribute => attribute.Name.LocalName == "ToolTip"))
                .Select(_ => Path.GetRelativePath(repositoryRoot, path).Replace('\\', '/')))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)];

        Assert.Empty(violations);
    }

    [Fact]
    public void ReleasePackaging_CapturesGitIdentityOutsideTheProcessPath()
    {
        string repositoryRoot = ResolveRepositoryRoot();
        string packageScript = File.ReadAllText(Path.Combine(repositoryRoot, "scripts", "package-release.ps1"));

        Assert.Contains("function Resolve-GitExecutable", packageScript, StringComparison.Ordinal);
        Assert.Contains("Git/cmd/git.exe", packageScript, StringComparison.Ordinal);
        Assert.Contains("throw \"Unable to capture the Git commit for release provenance.\"", packageScript, StringComparison.Ordinal);
    }

    [Fact]
    public void ApplicationManifests_DeclareOnlyWindows10AndLaterCompatibility()
    {
        const string expectedSupportedOs = "{8e0f7a12-bfb3-4fe8-b9a5-48fd50a15a9a}";
        string repositoryRoot = ResolveRepositoryRoot();

        foreach (string relativePath in new[] { "AudioPilot/app.manifest", "AudioPilot.CliHost/app.manifest" })
        {
            XDocument manifest = XDocument.Load(Path.Combine(repositoryRoot, relativePath));
            string[] supportedOperatingSystems =
            [..
                manifest
                .Descendants()
                .Where(static element => element.Name.LocalName == "supportedOS")
                .Select(static element => (string?)element.Attribute("Id"))
                .Where(static value => !string.IsNullOrWhiteSpace(value))
                .Cast<string>()
            ];

            Assert.Equal([expectedSupportedOs], supportedOperatingSystems);
        }
    }

    [Fact]
    public void SupportedWindowsFloor_IsAlignedAcrossBuildInstallerAndReleaseMetadata()
    {
        const string targetFramework = "net10.0-windows10.0.19041.0";
        const string minimumOsVersion = "10.0.19041.0";
        string repositoryRoot = ResolveRepositoryRoot();

        foreach (string relativePath in new[]
        {
            "AudioPilot/AudioPilot.csproj",
            "AudioPilot.CliHost/AudioPilot.CliHost.csproj",
            "AudioPilot.Tests/AudioPilot.Tests.csproj",
            "AudioPilot/Properties/PublishProfiles/FrameworkDependent-win-arm64.pubxml",
            "AudioPilot/Properties/PublishProfiles/FrameworkDependent-win-x64.pubxml",
            "AudioPilot/Properties/PublishProfiles/FrameworkDependent-win-x86.pubxml",
            "AudioPilot/Properties/PublishProfiles/SelfContained-win-arm64.pubxml",
            "AudioPilot/Properties/PublishProfiles/SelfContained-win-x64.pubxml",
            "AudioPilot/Properties/PublishProfiles/SelfContained-win-x86.pubxml",
        })
        {
            AssertFileContains(repositoryRoot, relativePath, targetFramework);
        }

        AssertFileContains(repositoryRoot, "AudioPilot.Installer/Package.wxs", "Name=\"CurrentBuildNumber\"");
        AssertFileContains(repositoryRoot, "AudioPilot.Installer/Package.wxs", "WINDOWSBUILDNUMBER &gt;= 19041");
        AssertFileContains(repositoryRoot, "packaging/winget/generate-winget-manifest.ps1", minimumOsVersion);
        AssertFileContains(repositoryRoot, "scripts/validate-winget-manifests.ps1", minimumOsVersion);
        AssertFileContains(repositoryRoot, "scripts/release-body.ps1", targetFramework);
        AssertFileContains(repositoryRoot, "README.md", "Minimum compatible OS: Windows 10 version 2004 (build 19041) or later");
        AssertFileContains(repositoryRoot, "README.md", "Official support: Windows 11 and currently supported Windows 10 Enterprise/LTSC editions");
        AssertFileContains(repositoryRoot, "docs/DEVELOPER_GUIDE.md", targetFramework);
        AssertFileContains(repositoryRoot, "docs/DEVELOPER_GUIDE.md", "The build floor expresses technical compatibility");
        AssertFileContains(repositoryRoot, "docs/RELEASING.md", minimumOsVersion);
        AssertFileContains(repositoryRoot, "docs/RELEASING.md", "technical minimum, not as blanket support");
        AssertFileContains(repositoryRoot, "scripts/release-body.ps1", "Minimum compatible OS");
        AssertFileContains(repositoryRoot, "scripts/release-body.ps1", "Official OS support");
    }

    [Fact]
    public void HardwareBoundAudioTestSessions_AreExplicitlyAttributedOutsidePortableCoverage()
    {
        string repositoryRoot = ResolveRepositoryRoot();
        const string implementation = "AudioPilot/Services/Audio/Testing/WasapiAudioEndpointTestSessions.cs";
        const string adapters = "AudioPilot/Services/Audio/Testing/AudioEndpointTestStreamAdapters.cs";

        AssertFileContains(repositoryRoot, implementation, "using System.Diagnostics.CodeAnalysis;");
        AssertFileContains(repositoryRoot, implementation, "Concrete WASAPI activation requires real Windows audio endpoints");
        AssertFileDoesNotContain(repositoryRoot, implementation, "Concrete WASAPI playback lifecycle requires a real Windows render endpoint");
        AssertFileDoesNotContain(repositoryRoot, implementation, "Concrete WASAPI capture and monitor lifecycle requires real Windows audio endpoints");
        AssertFileContains(repositoryRoot, adapters, "Thin forwarding adapter over NAudio's concrete WASAPI player");
        AssertFileContains(repositoryRoot, adapters, "Thin forwarding adapter over NAudio's concrete WASAPI recorder");
        AssertFileContains(repositoryRoot, adapters, "Concrete WASAPI monitor activation requires a real Windows render endpoint");
        AssertFileContains(repositoryRoot, "AudioPilot.Tests/Services/Audio/AudioEndpointTestHardwareTests.cs", "[HardwareSoakFact]");
    }

    [Fact]
    public void UiManifest_UsesPerMonitorDpiAwarenessWithoutLegacyGdiScaling()
    {
        string repositoryRoot = ResolveRepositoryRoot();
        XDocument manifest = XDocument.Load(Path.Combine(repositoryRoot, "AudioPilot", "app.manifest"));

        XElement dpiAwareness = Assert.Single(
            manifest.Descendants(),
            static element => element.Name.LocalName == "dpiAwareness");

        Assert.Equal("PerMonitorV2, PerMonitor", dpiAwareness.Value.Trim());
        Assert.DoesNotContain(manifest.Descendants(), static element => element.Name.LocalName == "gdiScaling");
    }

    [Fact]
    public void AudioEndpointTestPanels_TargetRowsAndExposeAccessibleInlineStatus()
    {
        string repositoryRoot = ResolveRepositoryRoot();
        XDocument output = XDocument.Load(Path.Combine(repositoryRoot, "AudioPilot", "Views", "OutputDevicePanel.xaml"));
        XDocument input = XDocument.Load(Path.Combine(repositoryRoot, "AudioPilot", "Views", "InputDevicePanel.xaml"));

        Assert.Contains(output.Descendants(), element =>
            element.Name.LocalName == "MenuItem" &&
            GetAttribute(element, "CommandParameter") == "{Binding DataContext}");
        Assert.Contains(input.Descendants(), element =>
            element.Name.LocalName == "MenuItem" &&
            GetAttribute(element, "CommandParameter") == "{Binding DataContext}");
        Assert.Contains(output.Descendants(), element =>
            GetAttribute(element, "AutomationProperties.LiveSetting") == "Polite");
        Assert.Contains(input.Descendants(), element =>
            GetAttribute(element, "AutomationProperties.LiveSetting") == "Polite");
        Assert.Contains(input.Descendants(), element =>
            element.Name.LocalName == "KeyBinding" && GetAttribute(element, "Key") == "Escape");
        XElement meter = Assert.Single(input.Descendants(), element => element.Name.LocalName == "AudioLevelMeter");
        Assert.Contains("Mode=OneWay", GetAttribute(meter, "Level"), StringComparison.Ordinal);
        Assert.Contains("Mode=OneWay", GetAttribute(meter, "Peak"), StringComparison.Ordinal);
        Assert.DoesNotContain(input.Descendants(), element => element.Name.LocalName == "ProgressBar");
        Assert.DoesNotContain(output.Descendants(), element =>
            (GetAttribute(element, "Header") ?? string.Empty).Contains("Remove from switch order", StringComparison.Ordinal));
        Assert.DoesNotContain(input.Descendants(), element =>
            (GetAttribute(element, "Header") ?? string.Empty).Contains("Remove from switch order", StringComparison.Ordinal));
        Assert.DoesNotContain(output.Descendants(), element =>
            (GetAttribute(element, "Command") ?? string.Empty).Contains("SelectedOutput", StringComparison.Ordinal));
        Assert.DoesNotContain(input.Descendants(), element =>
            (GetAttribute(element, "Command") ?? string.Empty).Contains("SelectedInput", StringComparison.Ordinal));
    }

    [Fact]
    public void XamlStyles_DoNotUseDynamicResourcesForBasedOn()
    {
        string repositoryRoot = ResolveRepositoryRoot();
        string[] xamlFiles = Directory.GetFiles(
            Path.Combine(repositoryRoot, "AudioPilot"),
            "*.xaml",
            SearchOption.AllDirectories);

        var violations = new List<string>();
        foreach (string xamlFile in xamlFiles)
        {
            XDocument document = XDocument.Load(xamlFile);
            foreach (XElement style in document.Descendants().Where(static element => element.Name.LocalName == "Style"))
            {
                string? basedOn = GetAttribute(style, "BasedOn");
                if (basedOn?.StartsWith("{DynamicResource ", StringComparison.Ordinal) == true)
                {
                    violations.Add(Path.GetRelativePath(repositoryRoot, xamlFile).Replace('\\', '/'));
                }
            }
        }

        Assert.Empty(violations);
    }

    [Fact]
    public void RepositoryText_UsesNeutralProfileNamesInWindowsPathFixtures()
    {
        string repositoryRoot = ResolveRepositoryRoot();
        string[] sourceRoots = ["AudioPilot", "AudioPilot.CliHost", "AudioPilot.Tests", "docs", ".github"];
        string[] textExtensions = [".cs", ".csproj", ".json", ".md", ".props", ".ps1", ".targets", ".xaml", ".xml", ".yaml", ".yml"];
        var violations = new List<string>();

        foreach (string sourceRoot in sourceRoots)
        {
            string absoluteSourceRoot = Path.Combine(repositoryRoot, sourceRoot);
            if (!Directory.Exists(absoluteSourceRoot))
            {
                continue;
            }

            foreach (string path in Directory.EnumerateFiles(absoluteSourceRoot, "*", SearchOption.AllDirectories))
            {
                string relativePath = Path.GetRelativePath(repositoryRoot, path).Replace('\\', '/');
                if (relativePath.Contains("/bin/", StringComparison.OrdinalIgnoreCase)
                    || relativePath.Contains("/obj/", StringComparison.OrdinalIgnoreCase)
                    || !textExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
                {
                    continue;
                }

                foreach (Match match in WindowsUserProfileRegex().Matches(File.ReadAllText(path)))
                {
                    string profileName = match.Groups["profile"].Value;
                    if (!string.Equals(profileName, "ExampleUser", StringComparison.Ordinal))
                    {
                        violations.Add($"{relativePath}: C:\\Users\\{profileName}");
                    }
                }
            }
        }

        Assert.True(
            violations.Count == 0,
            $"Windows profile path fixtures must use the neutral 'ExampleUser' identity:{Environment.NewLine}{string.Join(Environment.NewLine, violations)}");
    }

    [Fact]
    public void AnalyzerPolicy_DoesNotUsePragmasOrProjectWideNoWarnSuppressions()
    {
        const string pragmaWarningDirective = "#pragma" + " warning";
        string repositoryRoot = ResolveRepositoryRoot();
        string[] projectFiles = [.. Directory.GetFiles(repositoryRoot, "*.csproj", SearchOption.AllDirectories)
            .Where(static path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))];

        foreach (string projectFile in projectFiles)
        {
            Assert.DoesNotContain("<NoWarn>", File.ReadAllText(projectFile), StringComparison.Ordinal);
        }

        string[] sourceFiles = [.. Directory.GetFiles(repositoryRoot, "*.cs", SearchOption.AllDirectories)
            .Where(static path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .Where(static path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))];

        foreach (string sourceFile in sourceFiles)
        {
            Assert.DoesNotContain(pragmaWarningDirective, File.ReadAllText(sourceFile), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void DialogPolicy_ConfinesWpfMessageBoxesToEmergencyFallback()
    {
        string repositoryRoot = ResolveRepositoryRoot();
        string sourceRoot = Path.Combine(repositoryRoot, "AudioPilot");
        string allowedPath = Path.GetFullPath(Path.Combine(sourceRoot, "Services", "UI", "NativeAppDialogFallback.cs"));
        string[] prohibitedTokens = ["MessageBox.Show", "MessageBoxButton", "MessageBoxImage", "MessageBoxResult"];

        foreach (string path in Directory.GetFiles(sourceRoot, "*.cs", SearchOption.AllDirectories))
        {
            if (string.Equals(Path.GetFullPath(path), allowedPath, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string source = File.ReadAllText(path);
            foreach (string token in prohibitedTokens)
            {
                Assert.DoesNotContain(token, source, StringComparison.Ordinal);
            }
        }

        Assert.True(File.Exists(allowedPath));
    }

    [Fact]
    public void AppDialog_UsesMatchingThemeResourcesAndAccessibleLiveContent()
    {
        string repositoryRoot = ResolveRepositoryRoot();
        string dialogPath = Path.Combine(repositoryRoot, "AudioPilot", "AppDialogWindow.xaml");
        string dialogXaml = File.ReadAllText(dialogPath);
        string[] semanticResources =
        [
            "DialogIconBackgroundBrush",
            "DialogInformationBrush",
            "DialogSuccessBrush",
            "DialogWarningBrush",
            "DialogErrorBrush",
            "DialogQuestionBrush",
            "DialogPrimaryActionForegroundBrush",
            "DialogDestructiveActionForegroundBrush",
        ];

        Assert.Contains("IsReadOnly=\"True\"", dialogXaml, StringComparison.Ordinal);
        Assert.Contains("IsReadOnlyCaretVisible=\"True\"", dialogXaml, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.LiveSetting=\"Polite\"", dialogXaml, StringComparison.Ordinal);
        Assert.Contains("Copies only the displayed dialog message", dialogXaml, StringComparison.Ordinal);
        Assert.Contains("{DynamicResource WindowBackgroundBrush}", dialogXaml, StringComparison.Ordinal);
        Assert.Contains("{DynamicResource TextBrush}", dialogXaml, StringComparison.Ordinal);

        string dialogCode = File.ReadAllText(Path.Combine(repositoryRoot, "AudioPilot", "AppDialogWindow.xaml.cs"));
        Assert.Contains("RaiseAutomationEvent(AutomationEvents.LiveRegionChanged)", dialogCode, StringComparison.Ordinal);

        string fallbackCode = File.ReadAllText(Path.Combine(repositoryRoot, "AudioPilot", "Services", "UI", "NativeAppDialogFallback.cs"));
        Assert.Contains("ResolveOwnerForCurrentApplication", fallbackCode, StringComparison.Ordinal);
        Assert.Contains("MessageBox.Show(owner", fallbackCode, StringComparison.Ordinal);

        foreach (string themeFile in new[] { "LightTheme.xaml", "DarkTheme.xaml" })
        {
            string themeXaml = File.ReadAllText(Path.Combine(repositoryRoot, "AudioPilot", "Themes", themeFile));
            foreach (string resource in semanticResources)
            {
                Assert.Contains($"x:Key=\"{resource}\"", themeXaml, StringComparison.Ordinal);
            }
        }

        string themeHelper = File.ReadAllText(Path.Combine(repositoryRoot, "AudioPilot", "Platform", "WindowThemeHelper.cs"));
        foreach (string resource in semanticResources)
        {
            Assert.Contains($"\"{resource}\"", themeHelper, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ConfigurationWindows_ExposeNamesForFormControlsAndGlyphButtons()
    {
        string repositoryRoot = ResolveRepositoryRoot();
        string[] formControlNames = ["TextBox", "ComboBox", "ListBox", "Slider"];

        foreach (string fileName in ConfigurationWindowFiles)
        {
            XDocument document = XDocument.Load(Path.Combine(repositoryRoot, "AudioPilot", fileName));
            XElement[] unnamedFormControls =
            [..
                document
                .Descendants()
                .Where(element => formControlNames.Contains(element.Name.LocalName, StringComparer.Ordinal))
                .Where(static element => GetAttribute(element, "AutomationProperties.Name") == null)
            ];

            Assert.True(
                unnamedFormControls.Length == 0,
                $"{fileName} contains form controls without AutomationProperties.Name: {string.Join(", ", unnamedFormControls.Select(static element => element.Name.LocalName))}");

            XElement[] unnamedGlyphButtons =
            [..
                document
                .Descendants()
                .Where(static element => element.Name.LocalName == "Button")
                .Where(static element => GetAttribute(element, "AutomationProperties.Name") == null)
                .Where(static element => string.IsNullOrWhiteSpace(GetAttribute(element, "Content")))
            ];

            Assert.True(unnamedGlyphButtons.Length == 0, $"{fileName} contains an icon or templated button without an accessible name.");
        }
    }

    [Fact]
    public void ConfigurationWindows_AreResizableWithSensibleMinimumDimensions()
    {
        string repositoryRoot = ResolveRepositoryRoot();

        foreach (string fileName in new[] { "MainWindow.xaml", "RoutineEditorWindow.xaml" })
        {
            XDocument document = XDocument.Load(Path.Combine(repositoryRoot, "AudioPilot", fileName));
            XElement window = Assert.IsType<XElement>(document.Root);

            Assert.Equal("CanResizeWithGrip", GetAttribute(window, "ResizeMode"));
            Assert.True(ParseDimension(window, "MinWidth") >= 400d);
            Assert.True(ParseDimension(window, "MinHeight") >= 300d);
        }
    }

    [Fact]
    public void ImplicitButtonTemplates_RecognizeAccessKeysInEveryTheme()
    {
        string repositoryRoot = ResolveRepositoryRoot();
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

        foreach (string themeFile in new[] { "LightTheme.xaml", "DarkTheme.xaml" })
        {
            XDocument theme = XDocument.Load(Path.Combine(repositoryRoot, "AudioPilot", "Themes", themeFile));
            XElement buttonStyle = Assert.Single(
                theme.Descendants(),
                element => element.Name.LocalName == "Style"
                    && element.Attribute(xaml + "Key") == null
                    && GetAttribute(element, "TargetType") == "Button");

            Assert.Contains(
                buttonStyle.Descendants(),
                static element => element.Name.LocalName == "ContentPresenter"
                    && GetAttribute(element, "RecognizesAccessKey") == "True");
        }
    }

    [Fact]
    public void TrayMenu_UsesThemedAccessibleWpfMenuWithBoundedScrolling()
    {
        string repositoryRoot = ResolveRepositoryRoot();
        XDocument mainWindow = XDocument.Load(Path.Combine(repositoryRoot, "AudioPilot", "MainWindow.xaml"));
        string trayService = File.ReadAllText(Path.Combine(repositoryRoot, "AudioPilot", "Services", "UI", "AppTrayIconService.cs"));
        string runtimeHost = File.ReadAllText(Path.Combine(repositoryRoot, "AudioPilot", "AppRuntimeHost.cs"));
        string switchingViewModel = File.ReadAllText(Path.Combine(repositoryRoot, "AudioPilot", "ViewModels", "AppViewModel.Switching.cs"));

        Assert.DoesNotContain(mainWindow.Descendants(), static element => element.Name.LocalName == "TaskbarIcon");
        Assert.Contains("taskbarIcon = new TaskbarIcon", trayService, StringComparison.Ordinal);
        Assert.Contains("ownerWindow=false", trayService, StringComparison.Ordinal);
        Assert.Contains("AppTrayContextMenuStyle", trayService, StringComparison.Ordinal);
        Assert.Contains("RebindContextMenuTheme();", trayService, StringComparison.Ordinal);
        Assert.Contains("AudioPilot tray menu", trayService, StringComparison.Ordinal);

        int themeProviderIndex = runtimeHost.IndexOf("WindowThemeResolver.SetApplicationThemeProvider", StringComparison.Ordinal);
        int initialThemeIndex = runtimeHost.IndexOf("WindowThemeResolver.ApplyApplicationMainWindowTheme", StringComparison.Ordinal);
        int startupInitializationIndex = runtimeHost.IndexOf("await _startupResumeCoordinator.InitializeAsync", StringComparison.Ordinal);
        int trayPresentationIndex = runtimeHost.IndexOf("_trayService.EnsureVisible", StringComparison.Ordinal);
        Assert.True(themeProviderIndex >= 0);
        Assert.True(initialThemeIndex > themeProviderIndex);
        Assert.True(startupInitializationIndex > initialThemeIndex);
        Assert.True(trayPresentationIndex > startupInitializationIndex);
        Assert.Contains("_trayService.SchedulePresentationPrewarm();", runtimeHost, StringComparison.Ordinal);
        int preferenceHandlerIndex = runtimeHost.IndexOf("private void OnUserPreferenceChanged", StringComparison.Ordinal);
        int appearanceReapplyIndex = runtimeHost.IndexOf(
            "WindowThemeResolver.ApplyApplicationMainWindowTheme(_appVm.Theme);",
            preferenceHandlerIndex,
            StringComparison.Ordinal);
        int localeRefreshIndex = runtimeHost.IndexOf("if (localeChanged)", preferenceHandlerIndex, StringComparison.Ordinal);
        Assert.True(preferenceHandlerIndex >= 0);
        Assert.True(appearanceReapplyIndex > preferenceHandlerIndex);
        Assert.True(localeRefreshIndex > appearanceReapplyIndex);
        Assert.Contains("_shell.PrepareHiddenStartup", switchingViewModel, StringComparison.Ordinal);
        Assert.DoesNotContain("StartHiddenToTray(_shell.StartHiddenToTray", switchingViewModel, StringComparison.Ordinal);

        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        foreach (string themeFile in new[] { "LightTheme.xaml", "DarkTheme.xaml" })
        {
            XDocument theme = XDocument.Load(Path.Combine(repositoryRoot, "AudioPilot", "Themes", themeFile));
            XElement trayMenuStyle = Assert.Single(
                theme.Descendants(),
                element => string.Equals((string?)element.Attribute(xaml + "Key"), "AppTrayContextMenuStyle", StringComparison.Ordinal));
            XElement trayMenuItemStyle = Assert.Single(
                theme.Descendants(),
                element => string.Equals((string?)element.Attribute(xaml + "Key"), "AppTrayMenuItemStyle", StringComparison.Ordinal));

            Assert.Contains(trayMenuStyle.Descendants(), static element =>
                element.Name.LocalName == "ScrollViewer" && GetAttribute(element, "VerticalScrollBarVisibility") == "Auto");
            Assert.Contains(trayMenuStyle.Elements(), static element =>
                element.Name.LocalName == "Setter"
                && GetAttribute(element, "Property") == "OverridesDefaultStyle"
                && GetAttribute(element, "Value") == "True");
            Assert.Contains(trayMenuStyle.Elements(), static element =>
                element.Name.LocalName == "Setter"
                && GetAttribute(element, "Property") == "UseLayoutRounding"
                && GetAttribute(element, "Value") == "True");
            Assert.Contains(trayMenuStyle.Elements(), static element =>
                element.Name.LocalName == "Setter"
                && GetAttribute(element, "Property") == "MinWidth"
                && GetAttribute(element, "Value") == "248");
            Assert.Contains(trayMenuItemStyle.Elements(), static element =>
                element.Name.LocalName == "Setter"
                && GetAttribute(element, "Property") == "OverridesDefaultStyle"
                && GetAttribute(element, "Value") == "True");
            Assert.Contains(trayMenuItemStyle.Elements(), static element =>
                element.Name.LocalName == "Setter"
                && GetAttribute(element, "Property") == "MinHeight"
                && GetAttribute(element, "Value") == "32");
            Assert.Contains(trayMenuItemStyle.Elements(), static element =>
                element.Name.LocalName == "Setter"
                && GetAttribute(element, "Property") == "Padding"
                && GetAttribute(element, "Value") == "8,5");
            Assert.Contains(trayMenuItemStyle.Elements(), static element =>
                element.Name.LocalName == "Setter"
                && GetAttribute(element, "Property") == "FocusVisualStyle"
                && GetAttribute(element, "Value") == "{x:Null}");
            Assert.Contains(trayMenuItemStyle.Descendants(), static element =>
                element.Name.LocalName == "ControlTemplate" && GetAttribute(element, "TargetType") == "MenuItem");
            Assert.Contains(trayMenuItemStyle.Descendants(), static element =>
                element.Name.LocalName == "ColumnDefinition"
                && GetAttribute(element, "Name") == "IconColumn"
                && GetAttribute(element, "Width") == "16");
            Assert.Contains(trayMenuItemStyle.Descendants(), static element =>
                element.Name.LocalName == "TextBlock"
                && GetAttribute(element, "Name") == "GestureText"
                && GetAttribute(element, "Margin") == "12,0,4,0");
        }

        AssertFileDoesNotContain(repositoryRoot, Path.Combine("AudioPilot", "Services", "UI", "AppTrayIconService.cs"), "CreatePopupMenu");
    }

    [Fact]
    public void RuntimeHost_ShutdownDrainsApplicationOwnedResourcesBeforeDispatcherShutdown()
    {
        string repositoryRoot = ResolveRepositoryRoot();
        string runtimeHost = File.ReadAllText(Path.Combine(repositoryRoot, "AudioPilot", "AppRuntimeHost.cs"));
        string application = File.ReadAllText(Path.Combine(repositoryRoot, "AudioPilot", "App.xaml.cs"));

        int dialogDisposeIndex = runtimeHost.IndexOf("\"dispose-dialogs\"", StringComparison.Ordinal);
        int fallbackDialogDisposeIndex = runtimeHost.IndexOf("\"dispose-fallback-dialogs\"", StringComparison.Ordinal);
        int loggerDisposeIndex = runtimeHost.IndexOf("\"dispose-logger\"", StringComparison.Ordinal);
        int applicationShutdownIndex = runtimeHost.IndexOf("_application.Shutdown", loggerDisposeIndex, StringComparison.Ordinal);

        Assert.True(dialogDisposeIndex >= 0);
        Assert.True(fallbackDialogDisposeIndex > dialogDisposeIndex);
        Assert.True(loggerDisposeIndex > fallbackDialogDisposeIndex);
        Assert.True(applicationShutdownIndex > loggerDisposeIndex);
        Assert.Contains("if (_initialized)", runtimeHost, StringComparison.Ordinal);
        Assert.Contains("_logger.Dispose();", application, StringComparison.Ordinal);
    }

    [Fact]
    public void ApplicationContextMenus_ReuseTrayMenuTemplatesAndStyles()
    {
        string repositoryRoot = ResolveRepositoryRoot();
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        XDocument app = XDocument.Load(Path.Combine(repositoryRoot, "AudioPilot", "App.xaml"));
        XElement glyphStyle = Assert.Single(app.Descendants(), element =>
            string.Equals((string?)element.Attribute(xaml + "Key"), "AppMenuGlyphStyle", StringComparison.Ordinal));
        Assert.Contains(glyphStyle.Elements(), element =>
            element.Name.LocalName == "Setter"
            && GetAttribute(element, "Property") == "Stroke"
            && GetAttribute(element, "Value") == "{Binding (TextElement.Foreground), RelativeSource={RelativeSource Self}}");
        Assert.Contains(glyphStyle.Elements(), element =>
            element.Name.LocalName == "Setter"
            && GetAttribute(element, "Property") == "Focusable"
            && GetAttribute(element, "Value") == "False");
        Assert.Contains(glyphStyle.Elements(), element =>
            element.Name.LocalName == "Setter"
            && GetAttribute(element, "Property") == "IsHitTestVisible"
            && GetAttribute(element, "Value") == "False");

        XElement[] glyphResources =
        [..
            app.Descendants().Where(element =>
                element.Name.LocalName == "Path"
                && ((string?)element.Attribute(xaml + "Key"))?.StartsWith("AppMenu", StringComparison.Ordinal) == true
                && ((string?)element.Attribute(xaml + "Key"))?.EndsWith("Icon", StringComparison.Ordinal) == true)
        ];
        Assert.Equal(12, glyphResources.Length);
        foreach (XElement glyph in glyphResources)
        {
            Assert.Equal("False", (string?)glyph.Attribute(xaml + "Shared"));
            Assert.Null(GetAttribute(glyph, "AutomationProperties.Name"));
            Assert.Null(GetAttribute(glyph, "AutomationProperties.HelpText"));
            Assert.StartsWith("{x:Static controls:AppMenuGlyphs.", GetAttribute(glyph, "Data"), StringComparison.Ordinal);
            Assert.Equal("{StaticResource AppMenuGlyphStyle}", GetAttribute(glyph, "Style"));
        }

        foreach (string themeFile in new[] { "LightTheme.xaml", "DarkTheme.xaml" })
        {
            XDocument theme = XDocument.Load(Path.Combine(repositoryRoot, "AudioPilot", "Themes", themeFile));
            XElement contextMenuStyle = Assert.Single(theme.Descendants(), element =>
                string.Equals((string?)element.Attribute(xaml + "Key"), "AppContextMenuStyle", StringComparison.Ordinal));
            XElement menuItemStyle = Assert.Single(theme.Descendants(), element =>
                string.Equals((string?)element.Attribute(xaml + "Key"), "AppContextMenuItemStyle", StringComparison.Ordinal));
            XElement separatorStyle = Assert.Single(theme.Descendants(), element =>
                string.Equals((string?)element.Attribute(xaml + "Key"), "AppContextMenuSeparatorStyle", StringComparison.Ordinal));

            Assert.Equal("{StaticResource AppTrayContextMenuStyle}", GetAttribute(contextMenuStyle, "BasedOn"));
            Assert.Equal("{StaticResource AppTrayMenuItemStyle}", GetAttribute(menuItemStyle, "BasedOn"));
            Assert.Equal("{StaticResource AppTrayMenuSeparatorStyle}", GetAttribute(separatorStyle, "BasedOn"));
            Assert.Contains(contextMenuStyle.Elements(), element =>
                element.Name.LocalName == "Setter"
                && GetAttribute(element, "Property") == "MinWidth"
                && GetAttribute(element, "Value") == "240");
            Assert.Contains(menuItemStyle.Elements(), element =>
                element.Name.LocalName == "Setter"
                && GetAttribute(element, "Property") == "MinHeight"
                && GetAttribute(element, "Value") == "30");
            Assert.Contains(menuItemStyle.Elements(), element =>
                element.Name.LocalName == "Setter"
                && GetAttribute(element, "Property") == "Padding"
                && GetAttribute(element, "Value") == "8,4");

            XElement trayMenuItemStyle = Assert.Single(theme.Descendants(), element =>
                string.Equals((string?)element.Attribute(xaml + "Key"), "AppTrayMenuItemStyle", StringComparison.Ordinal));
            Assert.Contains(trayMenuItemStyle.Descendants(), element =>
                element.Name.LocalName == "Setter"
                && GetAttribute(element, "TargetName") == "IconColumn"
                && GetAttribute(element, "Property") == "Width"
                && GetAttribute(element, "Value") == "0");
            Assert.Contains(trayMenuItemStyle.Descendants(), element =>
                element.Name.LocalName == "ContentPresenter"
                && GetAttribute(element, "Name") == "IconPresenter"
                && GetAttribute(element, "TextElement.Foreground") == "{TemplateBinding Foreground}");
            Assert.Contains(trayMenuItemStyle.Elements(), element =>
                element.Name.LocalName == "Setter"
                && GetAttribute(element, "Property") == "MinHeight"
                && GetAttribute(element, "Value") == "32");
        }

        foreach (string relativeFile in ConfigurationWindowFiles)
        {
            XDocument document = XDocument.Load(Path.Combine(repositoryRoot, "AudioPilot", relativeFile));
            foreach (XElement contextMenu in document.Descendants().Where(static element => element.Name.LocalName == "ContextMenu"))
            {
                string? style = GetAttribute(contextMenu, "Style");
                Assert.True(
                    style is "{DynamicResource AppContextMenuStyle}" or "{DynamicResource AppTrayContextMenuStyle}",
                    $"Unexpected context-menu style '{style}' in {relativeFile}.");
            }

            foreach (XElement menuItem in document.Descendants().Where(static element => element.Name.LocalName == "MenuItem"))
            {
                Assert.Equal("{DynamicResource AppContextMenuItemStyle}", GetAttribute(menuItem, "Style"));
                Assert.StartsWith("{StaticResource AppMenu", GetAttribute(menuItem, "Icon"), StringComparison.Ordinal);
            }

            foreach (XElement separator in document.Descendants().Where(static element => element.Name.LocalName == "Separator"))
            {
                Assert.Equal("{DynamicResource AppContextMenuSeparatorStyle}", GetAttribute(separator, "Style"));
            }
        }

        AssertFileContains(repositoryRoot, "AudioPilot/Services/UI/AppTrayMenuBuilder.cs", "AppMenuGlyphs.Output");
        AssertFileDoesNotContain(repositoryRoot, "AudioPilot/Services/UI/AppTrayMenuBuilder.cs", "Geometry.Parse");
    }

    [Fact]
    public void RedactedLogExports_SanitizeArchiveContentInBothCliHosts()
    {
        string repositoryRoot = ResolveRepositoryRoot();
        string uiHost = File.ReadAllText(Path.Combine(repositoryRoot, "AudioPilot", "ViewModels", "AppViewModel.Cli.Diagnostics.cs"));
        string headlessHost = File.ReadAllText(Path.Combine(repositoryRoot, "AudioPilot.CliHost", "LocalHeadlessCommandRunner.ConfigDiagnostics.cs"));

        Assert.Contains("ExportLogs(AppDataPaths.GetWritableDataRoot(), fullPath, redactOutput)", uiHost, StringComparison.Ordinal);
        Assert.Contains("ExportLogs(GetLogRootDirectory(), fullPath, redactOutput)", headlessHost, StringComparison.Ordinal);
    }

    [Fact]
    public void UserFacingExports_CommitCompletedFilesAtomically()
    {
        string repositoryRoot = ResolveRepositoryRoot();
        string[] atomicExportSources =
        [
            "AudioPilot/Logging/DiagnosticBundleExportService.cs",
            "AudioPilot/Logging/LogArchiveExportService.cs",
            "AudioPilot/Services/Configuration/SettingsTransferService.cs",
            "AudioPilot.CliHost/LocalHeadlessCommandRunner.Routines.cs",
            "AudioPilot.CliHost/CliDocsMaintenance.cs",
        ];

        foreach (string relativePath in atomicExportSources)
        {
            string source = File.ReadAllText(Path.Combine(repositoryRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));
            Assert.Contains("AtomicFileWriter.", source, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void CiChangeFilter_CoversEveryBuildAndPackagingInput()
    {
        string repositoryRoot = ResolveRepositoryRoot();
        string workflow = File.ReadAllText(Path.Combine(repositoryRoot, ".github", "workflows", "ci.yml"));
        string[] requiredPaths =
        [
            "AudioPilot/**",
            "AudioPilot.CliHost/**",
            "AudioPilot.Installer/**",
            "AudioPilot.Tests/**",
            "packaging/**",
            "scripts/**",
            ".github/actions/**",
            ".github/quality/**",
            ".github/workflows/**",
            ".github/release-body-template.md",
            "README.md",
            "docs/CLI.md",
            "LICENSE",
            ".editorconfig",
            ".gitattributes",
            "Directory.Build.props",
            "Directory.Packages.props",
            "Version.props",
            "global.json",
            "nuget.config",
            "AudioPilot.sln",
            "AudioPilot.Format.slnf",
        ];

        foreach (string requiredPath in requiredPaths)
        {
            Assert.Contains($"- '{requiredPath}'", workflow, StringComparison.Ordinal);
        }

        Assert.Equal(
            4,
            WorkflowDispatchConditionRegex().Count(workflow));

        Assert.Matches(@"- name: Test\r?\n\s+if: github\.event_name != 'push'", workflow);
        Assert.Matches(@"- name: Format check\r?\n\s+if: github\.event_name != 'pull_request'", workflow);
    }

    [Fact]
    public void TestPlatform_UsesXunitV3WithMicrosoftTestingPlatformV2Conventions()
    {
        string repositoryRoot = ResolveRepositoryRoot();
        string globalJson = File.ReadAllText(Path.Combine(repositoryRoot, "global.json"));
        string packages = File.ReadAllText(Path.Combine(repositoryRoot, "Directory.Packages.props"));
        string testProject = File.ReadAllText(Path.Combine(repositoryRoot, "AudioPilot.Tests", "AudioPilot.Tests.csproj"));
        string testScript = File.ReadAllText(Path.Combine(repositoryRoot, "scripts", "run-tests.ps1"));
        string continuousIntegrationWorkflow = File.ReadAllText(Path.Combine(repositoryRoot, ".github", "workflows", "ci.yml"));
        string releaseWorkflow = File.ReadAllText(Path.Combine(repositoryRoot, ".github", "workflows", "release-artifacts.yml"));
        string localValidation = File.ReadAllText(Path.Combine(repositoryRoot, "scripts", "validate-all.ps1"));

        Assert.Contains("\"runner\": \"Microsoft.Testing.Platform\"", globalJson, StringComparison.Ordinal);
        XDocument packageVersions = XDocument.Parse(packages);
        foreach (string packageId in new[] { "xunit.v3", "xunit.runner.visualstudio" })
        {
            XElement package = Assert.Single(packageVersions.Descendants("PackageVersion"),
                element => (string?)element.Attribute("Include") == packageId);
            Assert.False(string.IsNullOrWhiteSpace((string?)package.Attribute("Version")));
        }
        Assert.Contains("Microsoft.Testing.Extensions.CodeCoverage", testProject, StringComparison.Ordinal);
        Assert.Contains("<UseMicrosoftTestingPlatformRunner>true</UseMicrosoftTestingPlatformRunner>", testProject, StringComparison.Ordinal);
        Assert.DoesNotContain("coverlet.collector", testProject, StringComparison.Ordinal);
        Assert.Contains("--project", testScript, StringComparison.Ordinal);
        Assert.Contains("--configuration", testScript, StringComparison.Ordinal);
        Assert.Contains("--no-build", testScript, StringComparison.Ordinal);
        Assert.Contains("--no-restore", testScript, StringComparison.Ordinal);
        Assert.Contains("--filter-query", testScript, StringComparison.Ordinal);
        Assert.Contains("--coverage-output-format", testScript, StringComparison.Ordinal);
        Assert.Contains("--minimum-expected-tests", testScript, StringComparison.Ordinal);
        Assert.Contains("--zero-tests-policy", testScript, StringComparison.Ordinal);
        Assert.Contains("validate-line-endings.ps1", continuousIntegrationWorkflow, StringComparison.Ordinal);
        Assert.Contains("validate-line-endings.ps1", localValidation, StringComparison.Ordinal);
        Assert.Contains("--report-xunit-trx", testScript, StringComparison.Ordinal);
        Assert.Contains("--coverage-settings", testScript, StringComparison.Ordinal);
        Assert.Contains("$env:AUDIOPILOT_RUN_INTEGRATION = \"1\"", testScript, StringComparison.Ordinal);
        Assert.Contains("$env:AUDIOPILOT_REQUIRE_INTEGRATION_HARDWARE = \"1\"", testScript, StringComparison.Ordinal);
        Assert.DoesNotContain("--collect:", testScript, StringComparison.Ordinal);
        Assert.DoesNotContain("--nologo", testScript, StringComparison.Ordinal);
        Assert.DoesNotContain("-DotnetTestArgs @(\"--nologo\"", continuousIntegrationWorkflow, StringComparison.Ordinal);
        Assert.DoesNotContain("-DotnetTestArgs @(\"--nologo\"", releaseWorkflow, StringComparison.Ordinal);
        Assert.DoesNotContain("-DotnetTestArgs @(\"--configuration\"", continuousIntegrationWorkflow, StringComparison.Ordinal);
        Assert.DoesNotContain("-DotnetTestArgs @(\"--configuration\"", releaseWorkflow, StringComparison.Ordinal);
        Assert.Contains("name: Upload unit test results", continuousIntegrationWorkflow, StringComparison.Ordinal);
        Assert.Contains("name: Upload integration test results", releaseWorkflow, StringComparison.Ordinal);
        Assert.Contains("name: Upload stress test results", releaseWorkflow, StringComparison.Ordinal);
    }

    [Fact]
    public void CoveragePolicy_UsesOneConfiguredProductionReportAndSharedValidator()
    {
        string repositoryRoot = ResolveRepositoryRoot();
        string testScript = File.ReadAllText(Path.Combine(repositoryRoot, "scripts", "run-tests.ps1"));
        string validator = File.ReadAllText(Path.Combine(repositoryRoot, "scripts", "validate-coverage.ps1"));
        string workflow = File.ReadAllText(Path.Combine(repositoryRoot, ".github", "workflows", "ci.yml"));
        string snapshotCollector = File.ReadAllText(Path.Combine(repositoryRoot, "AudioPilot", "Services", "Audio", "AudioSessionSnapshotCollector.cs"));
        XDocument settings = XDocument.Load(Path.Combine(repositoryRoot, ".github", "quality", "coverage.settings.xml"));

        Assert.Contains("AudioPilot.Tests-$SelectedCategory-coverage.cobertura.xml", testScript, StringComparison.Ordinal);
        Assert.Contains("Expected exactly one Cobertura report", validator, StringComparison.Ordinal);
        Assert.Contains("./scripts/validate-coverage.ps1", workflow, StringComparison.Ordinal);
        Assert.Contains(settings.Descendants(), static element =>
            element.Name.LocalName == "IncludeTestAssembly" && element.Value == "False");
        Assert.Contains(settings.Descendants(), static element =>
            element.Name.LocalName == "Source" && element.Value.Contains("obj", StringComparison.Ordinal));
        Assert.Contains("[ExcludeFromCodeCoverage(Justification = \"Hardware-dependent Core Audio session enumeration is exercised by opt-in integration and soak tests.\")]", snapshotCollector, StringComparison.Ordinal);
    }

    [Fact]
    public void InstallerPolicy_PersistsOptionsAndReleaseWorkflowUsesOneVersion()
    {
        string repositoryRoot = ResolveRepositoryRoot();
        string package = File.ReadAllText(Path.Combine(repositoryRoot, "AudioPilot.Installer", "Package.wxs"));
        string installerProject = File.ReadAllText(Path.Combine(repositoryRoot, "AudioPilot.Installer", "AudioPilot.Installer.wixproj"));
        string smoke = File.ReadAllText(Path.Combine(repositoryRoot, "scripts", "test-msi-smoke.ps1"));
        string optionMatrix = File.ReadAllText(Path.Combine(repositoryRoot, "scripts", "test-msi-option-matrix.ps1"));
        string integrity = File.ReadAllText(Path.Combine(repositoryRoot, "scripts", "validate-release-integrity.ps1"));
        string localRelease = File.ReadAllText(Path.Combine(repositoryRoot, "scripts", "build-local-release-artifacts.ps1"));
        string releaseWorkflow = File.ReadAllText(Path.Combine(repositoryRoot, ".github", "workflows", "release-artifacts.yml"));

        foreach (string property in new[]
        {
            "PREVIOUS_AUDIOPILOT_DATA_FOLDER",
            "PREVIOUS_INSTALLDESKTOPSHORTCUT",
            "PREVIOUS_INSTALLSTARTMENUSHORTCUT",
            "PREVIOUS_ADD_CLI_TO_PATH",
        })
        {
            Assert.Contains(property, package, StringComparison.Ordinal);
        }

        Assert.Contains("InstallDesktopShortcut", package, StringComparison.Ordinal);
        Assert.Contains("InstallStartMenuShortcut", package, StringComparison.Ordinal);
        Assert.Contains("AddCliToPath", package, StringComparison.Ordinal);
        Assert.Equal("afterInstallInitialize", (string?)XDocument.Parse(package).Descendants()
            .Single(element => element.Name.LocalName == "MajorUpgrade").Attribute("Schedule"));
        Assert.Contains(@"HKU\[UserSID]\Software\Microsoft\Windows\CurrentVersion\Run", package, StringComparison.Ordinal);
        Assert.Contains("/reg:64", package, StringComparison.Ordinal);
        Assert.Contains("Directory=\"System64Folder\"", package, StringComparison.Ordinal);
        Assert.Contains("Impersonate=\"no\"", package, StringComparison.Ordinal);
        Assert.Contains("Invoke-MsiInstallExpectFailure", smoke, StringComparison.Ordinal);
        Assert.Contains("Refusing to run the MSI smoke test while an existing $ProductName Run-at-startup value is present", smoke, StringComparison.Ordinal);
        Assert.Contains("Registry::HKEY_USERS\\$currentUserSid", smoke, StringComparison.Ordinal);
        Assert.Contains("$env:CODEX_CI", smoke, StringComparison.Ordinal);
        Assert.Contains("Test-UserPathContains", smoke, StringComparison.Ordinal);
        Assert.Contains("foreach ($desktop in @(\"0\", \"1\"))", optionMatrix, StringComparison.Ordinal);
        Assert.Contains("test-msi-option-matrix.ps1", releaseWorkflow, StringComparison.Ordinal);
        Assert.Contains("Upload MSI smoke logs", releaseWorkflow, StringComparison.Ordinal);
        Assert.Contains("Get-AudioPilotMsiSummaryProperty", integrity, StringComparison.Ordinal);
        Assert.Contains("-p:Version=$(AppVersion)", installerProject, StringComparison.Ordinal);
        Assert.Contains("-Version $effectiveVersion", localRelease, StringComparison.Ordinal);
        Assert.Contains("-p:AppVersion=$effectiveVersion", localRelease, StringComparison.Ordinal);
        Assert.Contains("resolve-release-version:", releaseWorkflow, StringComparison.Ordinal);
        Assert.Contains("needs.resolve-release-version.outputs.version", releaseWorkflow, StringComparison.Ordinal);
    }

    [Fact]
    public void LightAndDarkThemes_ExposeTheSameNamedResources()
    {
        string repositoryRoot = ResolveRepositoryRoot();
        string[] lightKeys = GetNamedResourceKeys(Path.Combine(repositoryRoot, "AudioPilot", "Themes", "LightTheme.xaml"));
        string[] darkKeys = GetNamedResourceKeys(Path.Combine(repositoryRoot, "AudioPilot", "Themes", "DarkTheme.xaml"));

        Assert.Equal(lightKeys, darkKeys);
    }

    [Theory]
    [InlineData("LightTheme.xaml")]
    [InlineData("DarkTheme.xaml")]
    public void ComboBoxPopup_UsesOneRoundedPixelAlignedSurfaceWithoutHorizontalChrome(string themeFile)
    {
        string repositoryRoot = ResolveRepositoryRoot();
        XDocument theme = XDocument.Load(Path.Combine(repositoryRoot, "AudioPilot", "Themes", themeFile));
        XElement comboBoxStyle = Assert.Single(
            theme.Root!.Elements(),
            static element => element.Name.LocalName == "Style" && GetAttribute(element, "TargetType") == "ComboBox");
        XElement popup = Assert.Single(
            comboBoxStyle.Descendants(),
            static element => element.Name.LocalName == "Popup" && GetAttribute(element, "Name") == "PART_Popup");
        XElement dropDown = Assert.Single(
            popup.Elements(),
            static element => element.Name.LocalName == "Border" && GetAttribute(element, "Name") == "DropDown");
        XElement scrollViewer = Assert.Single(
            dropDown.Elements(),
            static element => element.Name.LocalName == "ScrollViewer");

        Assert.Equal("True", GetAttribute(popup, "AllowsTransparency"));
        Assert.Equal("1", GetAttribute(dropDown, "BorderThickness"));
        Assert.Equal("True", GetAttribute(dropDown, "UseLayoutRounding"));
        Assert.Equal("True", GetAttribute(dropDown, "SnapsToDevicePixels"));
        Assert.Equal("Disabled", GetAttribute(scrollViewer, "HorizontalScrollBarVisibility"));
        Assert.Equal("Auto", GetAttribute(scrollViewer, "VerticalScrollBarVisibility"));
        Assert.Equal("True", GetAttribute(scrollViewer, "CanContentScroll"));
        Assert.DoesNotContain(popup.Elements(), static element => element.Name.LocalName == "Grid");
    }

    [Theory]
    [InlineData("MainWindow.xaml", "SettingsVolumeExpanderStyle")]
    [InlineData("RoutineEditorWindow.xaml", "RoutineEditorExpanderStyle")]
    public void ExpanderHeader_DoesNotCoverTheRoundedOuterSurface(string xamlFile, string styleKey)
    {
        string repositoryRoot = ResolveRepositoryRoot();
        XDocument document = XDocument.Load(Path.Combine(repositoryRoot, "AudioPilot", xamlFile));
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        XElement expanderStyle = Assert.Single(
            document.Descendants(),
            element => element.Name.LocalName == "Style" && (string?)element.Attribute(xaml + "Key") == styleKey);
        XElement headerTemplate = Assert.Single(
            expanderStyle.Descendants(),
            static element => element.Name.LocalName == "ControlTemplate" && GetAttribute(element, "TargetType") == "ToggleButton");
        XElement headerSurface = Assert.Single(
            headerTemplate.Elements(),
            static element => element.Name.LocalName == "Border");

        Assert.Equal("Transparent", GetAttribute(headerSurface, "Background"));
    }

    [Fact]
    public void TestHostInitialization_IsNonInteractiveAndDoesNotRejectRunningUiBeforeXunitStarts()
    {
        string repositoryRoot = ResolveRepositoryRoot();
        string initializerPath = Path.Combine("AudioPilot.Tests", "TestAssemblyInitializer.cs");

        AssertFileContains(repositoryRoot, initializerPath, "AppDialogService.SetDefaultPresenterForTests");
        AssertFileDoesNotContain(repositoryRoot, initializerPath, "EnsureNoRunningUiProcess");
        AssertFileContains(repositoryRoot, Path.Combine("scripts", "run-tests.ps1"), "AudioPilot UI is running");
    }

    [Theory]
    [InlineData("LightTheme.xaml", "#767676", "#FFFFFF")]
    [InlineData("DarkTheme.xaml", "#94949C", "#2D2D30")]
    public void PlaceholderText_MeetsNormalTextContrast(string themeFile, string expectedForeground, string background)
    {
        string repositoryRoot = ResolveRepositoryRoot();
        XDocument document = XDocument.Load(Path.Combine(repositoryRoot, "AudioPilot", "Themes", themeFile));
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        XElement placeholderBrush = Assert.Single(
            document.Descendants(),
            element => string.Equals((string?)element.Attribute(xaml + "Key"), "PlaceholderTextBrush", StringComparison.Ordinal));
        string foreground = Assert.IsType<string>((string?)placeholderBrush.Attribute("Color"));

        Assert.Equal(expectedForeground, foreground, ignoreCase: true);
        Assert.True(CalculateContrastRatio(foreground, background) >= 4.5d);
    }

    [Theory]
    [InlineData("LightTheme.xaml")]
    [InlineData("DarkTheme.xaml")]
    public void CheckBoxCheckMark_UsesDedicatedContrastSafeBrush(string themeFile)
    {
        string repositoryRoot = ResolveRepositoryRoot();
        XDocument document = XDocument.Load(Path.Combine(repositoryRoot, "AudioPilot", "Themes", themeFile));
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        XElement checkMarkBrush = Assert.Single(
            document.Descendants(),
            element => string.Equals((string?)element.Attribute(xaml + "Key"), "CheckMarkBrush", StringComparison.Ordinal));
        XElement optionMark = Assert.Single(
            document.Descendants(),
            element => string.Equals((string?)element.Attribute(xaml + "Name"), "optionMark", StringComparison.Ordinal));
        XElement accentBrush = Assert.Single(
            document.Descendants(),
            element => string.Equals((string?)element.Attribute(xaml + "Key"), "AccentBrush", StringComparison.Ordinal));

        string foreground = Assert.IsType<string>((string?)checkMarkBrush.Attribute("Color"));
        string background = Assert.IsType<string>((string?)accentBrush.Attribute("Color"));

        Assert.Equal("{DynamicResource CheckMarkBrush}", GetAttribute(optionMark, "Stroke"));
        Assert.True(CalculateContrastRatio(foreground, background) >= 4.5d);
    }

    [Theory]
    [InlineData("LightTheme.xaml", "AccentTextBrush", "ControlBackgroundBrush", 4.5d)]
    [InlineData("DarkTheme.xaml", "AccentTextBrush", "ControlBackgroundBrush", 4.5d)]
    // General borders are supplemental chrome: fill, labels, and the dedicated keyboard-focus
    // outline identify controls. Keep them visible without restoring the overly stark palette.
    [InlineData("LightTheme.xaml", "BorderBrush", "ControlBackgroundBrush", 1.5d)]
    [InlineData("DarkTheme.xaml", "BorderBrush", "ControlBackgroundBrush", 1.5d)]
    [InlineData("LightTheme.xaml", "HotkeyConflictBrush", "ControlBackgroundBrush", 4.5d)]
    [InlineData("LightTheme.xaml", "HotkeyReservedBrush", "ControlBackgroundBrush", 4.5d)]
    [InlineData("LightTheme.xaml", "HotkeyFallbackBrush", "ControlBackgroundBrush", 4.5d)]
    [InlineData("DarkTheme.xaml", "HotkeyConflictBrush", "ControlBackgroundBrush", 4.5d)]
    [InlineData("DarkTheme.xaml", "HotkeyReservedBrush", "ControlBackgroundBrush", 4.5d)]
    [InlineData("DarkTheme.xaml", "HotkeyFallbackBrush", "ControlBackgroundBrush", 4.5d)]
    [InlineData("LightTheme.xaml", "AccentSelectionHoverBrush", "#FFFFFF", 4.5d)]
    [InlineData("DarkTheme.xaml", "AccentSelectionHoverBrush", "#FFFFFF", 4.5d)]
    [InlineData("LightTheme.xaml", "RoutineWaitingBrush", "#FFFFFF", 4.5d)]
    [InlineData("DarkTheme.xaml", "RoutineWaitingBrush", "#FFFFFF", 4.5d)]
    [InlineData("LightTheme.xaml", "ResetButtonBackgroundBrush", "#FFFFFF", 4.5d)]
    [InlineData("LightTheme.xaml", "ResetButtonHoverBrush", "#FFFFFF", 4.5d)]
    [InlineData("LightTheme.xaml", "ResetButtonPressedBrush", "#FFFFFF", 4.5d)]
    [InlineData("DarkTheme.xaml", "ResetButtonBackgroundBrush", "#FFFFFF", 4.5d)]
    [InlineData("DarkTheme.xaml", "ResetButtonHoverBrush", "#FFFFFF", 4.5d)]
    [InlineData("DarkTheme.xaml", "ResetButtonPressedBrush", "#FFFFFF", 4.5d)]
    [InlineData("LightTheme.xaml", "TrayMenuHoverForegroundBrush", "TrayMenuHoverBackgroundBrush", 4.5d)]
    [InlineData("DarkTheme.xaml", "TrayMenuHoverForegroundBrush", "TrayMenuHoverBackgroundBrush", 4.5d)]
    [InlineData("LightTheme.xaml", "OverlaySuccessTextBrush", "#FFFFFF", 4.5d)]
    [InlineData("DarkTheme.xaml", "OverlaySuccessTextBrush", "#262628", 4.5d)]
    public void ThemeSemanticBrushPairs_MeetContrast(
        string themeFile,
        string foregroundBrushKey,
        string backgroundBrushKeyOrColor,
        double minimumContrast)
    {
        string repositoryRoot = ResolveRepositoryRoot();
        XDocument document = XDocument.Load(Path.Combine(repositoryRoot, "AudioPilot", "Themes", themeFile));
        string foreground = GetBrushColor(document, foregroundBrushKey);
        string background = backgroundBrushKeyOrColor.StartsWith('#')
            ? backgroundBrushKeyOrColor
            : GetBrushColor(document, backgroundBrushKeyOrColor);

        double contrast = CalculateContrastRatio(foreground, background);
        Assert.True(
            contrast >= minimumContrast,
            $"{themeFile} {foregroundBrushKey} ({foreground}) against {backgroundBrushKeyOrColor} ({background}) has contrast {contrast:0.00}; expected at least {minimumContrast:0.00}.");
    }

    private static string? GetAttribute(XElement element, string localName)
    {
        return element.Attributes().FirstOrDefault(attribute => string.Equals(attribute.Name.LocalName, localName, StringComparison.Ordinal))?.Value;
    }

    private static string? TryParseUsingNamespace(string line, string prefix)
    {
        string trimmed = line.Trim();
        if (!trimmed.StartsWith(prefix, StringComparison.Ordinal)
            || !trimmed.EndsWith(';')
            || trimmed.Contains('=')
            || trimmed.StartsWith($"{prefix}static ", StringComparison.Ordinal))
        {
            return null;
        }

        return trimmed[prefix.Length..^1].Trim();
    }

    private static string[] GetNamedResourceKeys(string themePath)
    {
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        return
        [..
            XDocument
                .Load(themePath)
                .Descendants()
                .Select(element => (string?)element.Attribute(xaml + "Key"))
                .Where(static key => !string.IsNullOrWhiteSpace(key))
                .Cast<string>()
                .Order(StringComparer.Ordinal)
        ];
    }

    private static string GetBrushColor(XDocument document, string key)
    {
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        XElement brush = Assert.Single(
            document.Descendants(),
            element => string.Equals((string?)element.Attribute(xaml + "Key"), key, StringComparison.Ordinal));
        return Assert.IsType<string>((string?)brush.Attribute("Color"));
    }

    private static void AssertFileContains(string repositoryRoot, string relativePath, string expected)
    {
        string content = File.ReadAllText(Path.Combine(repositoryRoot, relativePath));
        Assert.Contains(expected, content, StringComparison.Ordinal);
    }

    private static void AssertFileDoesNotContain(string repositoryRoot, string relativePath, string unexpected)
    {
        string content = File.ReadAllText(Path.Combine(repositoryRoot, relativePath));
        Assert.DoesNotContain(unexpected, content, StringComparison.Ordinal);
    }

    private static double ParseDimension(XElement element, string attributeName)
    {
        return double.Parse(Assert.IsType<string>(GetAttribute(element, attributeName)), CultureInfo.InvariantCulture);
    }

    private static double CalculateContrastRatio(string foreground, string background)
    {
        double foregroundLuminance = CalculateRelativeLuminance(foreground);
        double backgroundLuminance = CalculateRelativeLuminance(background);
        return (Math.Max(foregroundLuminance, backgroundLuminance) + 0.05d) /
            (Math.Min(foregroundLuminance, backgroundLuminance) + 0.05d);
    }

    private static double CalculateRelativeLuminance(string color)
    {
        string hex = color.TrimStart('#');
        Assert.True(
            hex.Length is 6 or 8,
            $"Expected an RGB or ARGB hexadecimal color, but received '{color}'.");
        int rgbOffset = hex.Length == 8 ? 2 : 0;
        double[] channels =
        [
            int.Parse(hex.AsSpan(rgbOffset, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture) / 255d,
            int.Parse(hex.AsSpan(rgbOffset + 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture) / 255d,
            int.Parse(hex.AsSpan(rgbOffset + 4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture) / 255d,
        ];

        for (int index = 0; index < channels.Length; index++)
        {
            channels[index] = channels[index] <= 0.04045d
                ? channels[index] / 12.92d
                : Math.Pow((channels[index] + 0.055d) / 1.055d, 2.4d);
        }

        return (0.2126d * channels[0]) + (0.7152d * channels[1]) + (0.0722d * channels[2]);
    }

    [GeneratedRegex(@"C:\\Users\\(?<profile>[^\\\s""']+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex WindowsUserProfileRegex();

    private static string ResolveRepositoryRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current != null)
        {
            if (File.Exists(Path.Combine(current.FullName, "AudioPilot.sln")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the AudioPilot repository root.");
    }

    [GeneratedRegex("if: github\\.event_name == 'workflow_dispatch' \\|\\| needs\\.changes\\.outputs\\.", RegexOptions.CultureInvariant)]
    private static partial Regex WorkflowDispatchConditionRegex();
}
