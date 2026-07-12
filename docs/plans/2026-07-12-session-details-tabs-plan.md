# Session Details Tabs + Themed Close Dialog Implementation Plan
> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Reorganize the Session Details window from one long vertical scroll into two tabs (Details / Speakers) under a persistent Save/Discard header, and replace the plain Windows `MessageBox` unsaved-changes close prompt with a themed Fluent `Wpf.Ui.Controls.MessageBox`.

**Architecture:** This is a pure view/code-behind reorganization: `MetadataEditorViewModel` is UNCHANGED. The buffered edit model (one working copy, `SaveCommand` commits everything, `IsDirty` spans all fields) already covers every card, so tabs are presentational and Save/Discard/dirty tracking work identically from either tab. Task 1 rewrites `SessionDetailsWindow.xaml` (persistent header + `TabControl`); Task 2 rewrites the close guard in `SessionDetailsWindow.xaml.cs` to await a themed dialog via an async helper (WPF cannot await inside `OnClosing`).

**Tech Stack:** C#/.NET 10, WPF, CommunityToolkit.Mvvm, Wpf.Ui, xUnit.

## Global Constraints
- Target branch: `feat/session-details-tabs` (the design spec `docs/plans/2026-07-12-session-details-tabs-design.md` is already committed there).
- 0-warning build gate must hold.
- Tests: xUnit. Run a filtered test with:
  `dotnet test "tests\LocalScribe.App.Tests\LocalScribe.App.Tests.csproj" --filter "FullyQualifiedName~<Name>" --nologo`
- IMPORTANT: the LocalScribe app may be running and LOCK its bin DLL/exe (MSB3027 copy error — NOT a compile error). When that happens, build/test to an isolated output so the lock is avoided: append
  `-p:BaseOutputPath=C:\Users\SAMUE~1.SAM\AppData\Local\Temp\claude\F--LocalScribe\4c069d5c-1598-457e-ab60-c2de6a9d7c1e\scratchpad\isobin\`
  to the `dotnet test` command. Never kill the user's app.
- Never use Unicode emojis in test code or scripts (project rule).
- Commit messages follow the repo style: `fix(app)`/`feat(app)`/`test(app)`/`docs(...)`. Every commit message MUST end with these two trailer lines EXACTLY:
```
Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01X6u4L9ZxYKCuvxGBbrJrrW
```

### Why there is no red unit test in either task
The design spec states explicitly: **"No view-model change."** All behavior (`IsDirty`, `SaveCommand`, `DiscardCommand`, every field/participant binding) is VM-level and already unit-tested; this feature moves controls between panels and swaps a dialog implementation. There is no new headless-testable seam. Per the plan-writing rules for UI-only changes, each task's automated gate is: **build 0-warning + the full App test suite green (which builds `LocalScribe.App` and so catches any XAML/C# compile break) + `XamlHygieneTests` green**, and its behavioral gate is a **precise manual smoke**. The App.Tests project references the App project, so `dotnet test` compiles the window — a broken XAML or code-behind fails the test build.

### Verified facts this plan is grounded in (read before starting)
- Wpf.Ui package: `WPF-UI` **4.0.3** (`src\LocalScribe.App\LocalScribe.App.csproj:13`).
- `Wpf.Ui.Controls.MessageBox` (verified by reflection against `wpf-ui/4.0.3/lib/net9.0-windows7.0/Wpf.Ui.dll`):
  - Derives from `System.Windows.Window`; has a public parameterless ctor.
  - Settable: `Title` (string, from `Window`), `Content` (object, from `ContentControl`), `PrimaryButtonText`, `SecondaryButtonText`, `CloseButtonText` (all string).
  - `Task<MessageBoxResult> ShowDialogAsync(bool showAsDialog = true, CancellationToken = default)` — callable as `await dialog.ShowDialogAsync()`.
  - `Wpf.Ui.Controls.MessageBoxResult` enum = `{ None, Primary, Secondary }`. Primary button -> `Primary`; Secondary button -> `Secondary`; Close button / Esc / title-bar close -> `None`.
- `App.xaml` merges `<ui:ThemesDictionary />` + `<ui:ControlsDictionary />` (`App.xaml:9-10`), so the themed `MessageBox` renders with the Fluent template. The prompt is shown only on a user close action (long after the message pump is up), so the Wpf.Ui "Mica window shown before the pump renders invisible" gotcha does not apply.
- `SectionCard` and `MutedText` styles live in the app-global `Styles\Fluent.Shared.xaml` (merged in `App.xaml:11`), so `{StaticResource SectionCard}` / `{StaticResource MutedText}` resolve anywhere inside the window regardless of which tab a control sits in.
- `x:Name="DetailPane"` (current `SessionDetailsWindow.xaml:31`) is NOT referenced from code-behind or tests (only old plan docs), so removing that outer `ScrollViewer` is safe.
- `MetadataEditorViewModel` exposes exactly what the view binds: `Title`, `Description`, `SelectedMedium`, `MediumOptions` (static), `MatterSearchText`, `TaggedMatters`, `MatterOptions`, `CanCreateMatterFromSearch`, `CreateMatterCommand`, `ToggleMatterCommand`, `Archived`, `IsDirty`, `IsEditable`, `LockHint`, `DiariseHint`, `DiariseCommand`, `LocalParticipants`/`RemoteParticipants`, `RemoveParticipantCommand`, `AddLocal*/AddRemote*` commands, `NewLocalName`/`NewRemoteName`, `RosterPicks`, `LocalSelectedRosterPick`/`RemoteSelectedRosterPick`, `SaveCommand`, `DiscardCommand`, and `RenameParticipant(ParticipantRow, string)` (called from the `LostFocus` handler and the close guard). None of these change.

---

## Task 1: Split the window body into a persistent header + Details/Speakers TabControl

**Files:**
- Modify (full body rewrite, current `:31-236`, i.e. everything between the `ui:TitleBar` and the closing `</DockPanel>`): `src/LocalScribe.App/SessionDetailsWindow.xaml`
- Test (gate): `tests/LocalScribe.App.Tests/XamlHygieneTests.cs` (must stay green — no edit) + full App suite build-green + manual smoke.

**Interfaces:**
- Consumes (all already exist on `MetadataEditorViewModel`, unchanged): `Title`, `Description`, `SelectedMedium`, `MetadataEditorViewModel.MediumOptions`, `MatterSearchText`, `CanCreateMatterFromSearch`, `CreateMatterCommand`, `TaggedMatters`, `MatterOptions`, `ToggleMatterCommand`, `Archived`, `IsDirty`, `IsEditable`, `LockHint`, `DiariseHint`, `DiariseCommand`, `LocalParticipants`, `RemoteParticipants`, `NewLocalName`, `NewRemoteName`, `AddLocalNameCommand`, `AddRemoteNameCommand`, `AddLocalUnnamedCommand`, `AddRemoteUnnamedCommand`, `AddLocalFromRosterCommand`, `AddRemoteFromRosterCommand`, `RosterPicks`, `LocalSelectedRosterPick`, `RemoteSelectedRosterPick`, `RemoveParticipantCommand`, `SaveCommand`, `DiscardCommand`. Event handler `OnParticipantNameCommitted` (code-behind, unchanged in this task).
- Produces: no new public names. Structural markers preserved for tests: the root `DockPanel` keeps `TextElement.Foreground="{DynamicResource TextFillColorPrimaryBrush}"` (asserted by `XamlHygieneTests.PageAndWindowRoots_SetInheritableForeground`); no hardcoded ARGB brushes added (asserted by `ShippedXaml_HasNoDisallowedHardcodedBrushes`); no app-global implicit `TextBlock` style added.

### Steps

- [ ] **Baseline green.** Confirm the suite passes before touching anything:
```
dotnet test "tests\LocalScribe.App.Tests\LocalScribe.App.Tests.csproj" --filter "FullyQualifiedName~XamlHygiene" --nologo -p:BaseOutputPath=C:\Users\SAMUE~1.SAM\AppData\Local\Temp\claude\F--LocalScribe\4c069d5c-1598-457e-ab60-c2de6a9d7c1e\scratchpad\isobin\
```
Expect: all `XamlHygieneTests` pass (baseline). If the App exe is locked, the isolated `BaseOutputPath` above avoids MSB3027.

- [ ] **Rewrite the window.** Replace the ENTIRE contents of `src/LocalScribe.App/SessionDetailsWindow.xaml` with the following. Changes vs. current: the outer `ScrollViewer x:Name="DetailPane"` + single `StackPanel` are gone; the Save/Discard/dirty controls + `LockHint` become a persistent `DockPanel.Dock="Top"` header (the in-content "Details" heading is dropped); the three cards are distributed across a two-`TabItem` `TabControl`; the **Archived** checkbox moves out of the Speakers card into the Details tab; and `IsEnabled="{Binding IsEditable}"` now sits on each tab's content `StackPanel`. Every card's inner content is lifted verbatim.
```xml
<ui:FluentWindow x:Class="LocalScribe.App.SessionDetailsWindow"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        xmlns:ui="http://schemas.lepo.co/wpfui/2022/xaml"
        xmlns:vm="clr-namespace:LocalScribe.App.ViewModels"
        Title="Session details" Height="640" Width="460" MinHeight="400" MinWidth="380"
        Icon="pack://application:,,,/Assets/LocalScribe.ico"
        WindowBackdropType="Mica" ExtendsContentIntoTitleBar="True">
    <Window.Resources>
        <BooleanToVisibilityConverter x:Key="BoolToVis" />
        <!-- Same chip pattern as Pages/SessionsPage.xaml (Stage 5.3) - theme brushes only. -->
        <Style x:Key="Chip" TargetType="Border">
            <Setter Property="CornerRadius" Value="8" />
            <Setter Property="Padding" Value="6,1" />
            <Setter Property="Margin" Value="0,0,4,0" />
            <Setter Property="Background" Value="{DynamicResource ControlFillColorSecondaryBrush}" />
        </Style>
    </Window.Resources>
    <ui:FluentWindow.InputBindings>
        <!-- Stage 5.4 5.1: keyboard save. Title/Description below bind PropertyChanged, so the
             working copy is already current when this fires. Window-scoped: unaffected by tabs. -->
        <KeyBinding Modifiers="Control" Key="S" Command="{Binding SaveCommand}" />
    </ui:FluentWindow.InputBindings>
    <DockPanel TextElement.Foreground="{DynamicResource TextFillColorPrimaryBrush}">
        <ui:TitleBar DockPanel.Dock="Top" Title="Session details"
                     ShowMinimize="True" ShowMaximize="True" ShowClose="True">
            <ui:TitleBar.Icon>
                <ui:ImageIcon Source="pack://application:,,,/Assets/LocalScribe.ico" />
            </ui:TitleBar.Icon>
        </ui:TitleBar>

        <!-- Persistent Save/Discard header ABOVE the tabs so it governs BOTH: LockHint on the left,
             the dirty indicator + Discard + Save on the right. The old in-content "Details" heading
             is dropped as redundant with the title bar's "Session details". NOT gated by IsEditable:
             Save's own CanExecute (IsDirty && IsEditable) already greys it on a locked session. -->
        <DockPanel DockPanel.Dock="Top" Margin="16,16,16,8">
            <StackPanel DockPanel.Dock="Right" Orientation="Horizontal" VerticalAlignment="Center">
                <TextBlock Text="Unsaved changes" Style="{StaticResource MutedText}"
                           VerticalAlignment="Center" Margin="0,0,8,0"
                           Visibility="{Binding IsDirty, Converter={StaticResource BoolToVis}}" />
                <ui:Button Content="Discard" Appearance="Secondary"
                           Command="{Binding DiscardCommand}" Margin="0,0,8,0" />
                <ui:Button Content="Save" Appearance="Primary"
                           Command="{Binding SaveCommand}" />
            </StackPanel>
            <TextBlock Text="{Binding LockHint}" Style="{StaticResource MutedText}"
                       VerticalAlignment="Center" TextWrapping="Wrap" />
        </DockPanel>

        <!-- Two tabs replace the single long scroll. Details is short; Speakers can grow tall with
             many participants, so each tab body owns its OWN ScrollViewer. IsEditable gates each
             tab's CONTENT panel (not the TabControl), so a live/pending session shows both tabs
             read-only but still switchable. -->
        <TabControl Margin="16,0,16,16">
            <TabItem Header="Details">
                <ScrollViewer VerticalScrollBarVisibility="Auto">
                    <StackPanel IsEnabled="{Binding IsEditable}" Margin="4,8,4,4">
                        <!-- Session -->
                        <ui:Card Style="{StaticResource SectionCard}" Margin="0,0,0,12">
                            <StackPanel>
                                <TextBlock Text="Session" FontWeight="SemiBold" Margin="0,0,0,8" />
                                <TextBlock Text="Title" Margin="0,0,0,4" />
                                <TextBox Text="{Binding Title, UpdateSourceTrigger=PropertyChanged}" />
                                <TextBlock Text="Description" Margin="0,12,0,4" />
                                <TextBox Text="{Binding Description, UpdateSourceTrigger=PropertyChanged}"
                                         AcceptsReturn="True" MinLines="3" TextWrapping="Wrap" />
                            </StackPanel>
                        </ui:Card>

                        <!-- Classification -->
                        <ui:Card Style="{StaticResource SectionCard}">
                            <StackPanel>
                                <TextBlock Text="Classification" FontWeight="SemiBold" Margin="0,0,0,8" />
                                <TextBlock Text="Call type" Margin="0,0,0,4" />
                                <ComboBox ItemsSource="{x:Static vm:MetadataEditorViewModel.MediumOptions}"
                                          DisplayMemberPath="Display" SelectedValuePath="Value"
                                          SelectedValue="{Binding SelectedMedium}" />
                                <TextBlock Text="Matters" Margin="0,12,0,4" />
                                <ui:TextBox PlaceholderText="Search matters by name, reference, or id..."
                                            Text="{Binding MatterSearchText, UpdateSourceTrigger=PropertyChanged}" />
                                <!-- Tagged chips: ALWAYS the full tagged set (selection truth), independent
                                     of the search filter below. The x routes through the same buffered
                                     ToggleMatter path (design 5.3/5.1) - nothing writes until Save. -->
                                <ItemsControl ItemsSource="{Binding TaggedMatters}" Margin="0,6,0,0">
                                    <ItemsControl.ItemsPanel>
                                        <ItemsPanelTemplate><WrapPanel /></ItemsPanelTemplate>
                                    </ItemsControl.ItemsPanel>
                                    <ItemsControl.ItemTemplate>
                                        <DataTemplate>
                                            <Border Style="{StaticResource Chip}" Margin="0,0,4,4">
                                                <StackPanel Orientation="Horizontal">
                                                    <TextBlock Text="{Binding Display}" VerticalAlignment="Center" />
                                                    <ui:Button Margin="4,0,0,0" Padding="2,0" Appearance="Transparent"
                                                               ToolTip="Remove tag (takes effect on Save)"
                                                               Command="{Binding DataContext.ToggleMatterCommand,
                                                                         RelativeSource={RelativeSource AncestorType=ItemsControl}}"
                                                               CommandParameter="{Binding}">
                                                        <ui:SymbolIcon Symbol="Dismiss16" />
                                                    </ui:Button>
                                                </StackPanel>
                                            </Border>
                                        </DataTemplate>
                                    </ItemsControl.ItemTemplate>
                                </ItemsControl>
                                <!-- Bounded, scrollable results: usable at 100+ matters (design 5.3).
                                     Checkbox rows keep the tag state visible inline; toggling stays the
                                     buffered ToggleMatter path. -->
                                <Border BorderThickness="1" CornerRadius="4" Margin="0,6,0,0"
                                        BorderBrush="{DynamicResource ControlStrokeColorDefaultBrush}">
                                    <ScrollViewer MaxHeight="160" VerticalScrollBarVisibility="Auto">
                                        <ItemsControl ItemsSource="{Binding MatterOptions}" Margin="8,4">
                                            <ItemsControl.ItemTemplate>
                                                <DataTemplate>
                                                    <CheckBox Content="{Binding Display}" IsChecked="{Binding IsSelected, Mode=OneWay}"
                                                              Command="{Binding DataContext.ToggleMatterCommand,
                                                                        RelativeSource={RelativeSource AncestorType=ItemsControl}}"
                                                              CommandParameter="{Binding}" />
                                                </DataTemplate>
                                            </ItemsControl.ItemTemplate>
                                        </ItemsControl>
                                    </ScrollViewer>
                                </Border>
                                <!-- Inline create: offered exactly when the search matches nothing. The
                                     matter is created immediately (additive); the TAG is buffered until Save. -->
                                <ui:Button Appearance="Secondary" HorizontalAlignment="Left" Margin="0,6,0,0"
                                           Command="{Binding CreateMatterCommand}"
                                           Visibility="{Binding CanCreateMatterFromSearch, Converter={StaticResource BoolToVis}}">
                                    <TextBlock Text="{Binding MatterSearchText, StringFormat='Create matter &quot;{0}&quot;'}" />
                                </ui:Button>
                            </StackPanel>
                        </ui:Card>

                        <!-- Archived relocated here from the Speakers card: it is a SESSION property,
                             not a speaker one (design). Same binding, unchanged buffered-Save path. -->
                        <CheckBox Content="Archived (hidden from the default list)"
                                  IsChecked="{Binding Archived}" Margin="4,12,0,0" />
                    </StackPanel>
                </ScrollViewer>
            </TabItem>

            <TabItem Header="Speakers">
                <ScrollViewer VerticalScrollBarVisibility="Auto">
                    <StackPanel IsEnabled="{Binding IsEditable}" Margin="4,8,4,4">
                        <!-- ONE unified identity-first card (Stage 5.4 5.2). Each side is a list of
                             speaker slots - named people plus explicit "Speaker N" unnamed slots. The
                             persisted per-side counts derive from these slot lists at Save time. Split
                             speakers is disabled while the editor is dirty (DiariseHint says why). -->
                        <ui:Card Style="{StaticResource SectionCard}">
                            <StackPanel>
                                <DockPanel Margin="0,0,0,4">
                                    <ui:Button DockPanel.Dock="Right" Content="Split speakers..."
                                               Command="{Binding DiariseCommand}" />
                                    <TextBlock Text="Speakers" FontWeight="SemiBold" VerticalAlignment="Center" />
                                </DockPanel>
                                <TextBlock Text="{Binding DiariseHint}" Style="{StaticResource MutedText}"
                                           Margin="0,0,0,8" />
                                <Grid>
                                    <Grid.ColumnDefinitions>
                                        <ColumnDefinition Width="*" />
                                        <ColumnDefinition Width="*" />
                                    </Grid.ColumnDefinitions>
                                    <StackPanel Grid.Column="0" Margin="0,0,6,0">
                                        <TextBlock Text="Local" FontWeight="SemiBold" Margin="0,0,0,4" />
                                        <ItemsControl ItemsSource="{Binding LocalParticipants}">
                                            <ItemsControl.ItemTemplate>
                                                <DataTemplate>
                                                    <DockPanel Margin="0,2,0,2">
                                                        <ui:Button DockPanel.Dock="Right" Content="Remove" Appearance="Secondary"
                                                                   Command="{Binding DataContext.RemoveParticipantCommand,
                                                                             RelativeSource={RelativeSource AncestorType=ItemsControl}}"
                                                                   CommandParameter="{Binding}" />
                                                        <TextBlock DockPanel.Dock="Right" Text=" (you)" Margin="4,0,0,0"
                                                                   VerticalAlignment="Center" Style="{StaticResource MutedText}"
                                                                   Visibility="{Binding IsSelf, Converter={StaticResource BoolToVis}}" />
                                                        <!-- Rename a slot IN PLACE. Seed once from Name; an unnamed slot
                                                             shows its "Speaker N" label as a placeholder. LostFocus commits
                                                             via OnParticipantNameCommitted -> VM.RenameParticipant. -->
                                                        <ui:TextBox Text="{Binding Name, Mode=OneTime}"
                                                                    PlaceholderText="{Binding DisplayLabel, Mode=OneWay}"
                                                                    ToolTip="Type a name to identify this speaker; clear it to leave it unnamed."
                                                                    LostFocus="OnParticipantNameCommitted"
                                                                    IsEnabled="{Binding DataContext.IsEditable, RelativeSource={RelativeSource AncestorType=ItemsControl}}"
                                                                    VerticalAlignment="Center" />
                                                    </DockPanel>
                                                </DataTemplate>
                                            </ItemsControl.ItemTemplate>
                                        </ItemsControl>
                                        <DockPanel Margin="0,6,0,0">
                                            <ui:Button DockPanel.Dock="Right" Content="Add" Appearance="Primary"
                                                       Command="{Binding AddLocalNameCommand}" Margin="6,0,0,0" />
                                            <ui:TextBox PlaceholderText="Add local speaker"
                                                        Text="{Binding NewLocalName, UpdateSourceTrigger=PropertyChanged}" />
                                        </DockPanel>
                                        <ui:Button Content="Add unnamed speaker" Appearance="Secondary" Margin="0,6,0,0"
                                                   HorizontalAlignment="Stretch"
                                                   Command="{Binding AddLocalUnnamedCommand}" />
                                        <DockPanel Margin="0,6,0,0">
                                            <ui:Button DockPanel.Dock="Right" Content="Add from roster" Appearance="Secondary"
                                                       Command="{Binding AddLocalFromRosterCommand}" Margin="6,0,0,0" />
                                            <ComboBox ItemsSource="{Binding RosterPicks}" DisplayMemberPath="Display"
                                                      SelectedItem="{Binding LocalSelectedRosterPick, Mode=TwoWay}" />
                                        </DockPanel>
                                    </StackPanel>
                                    <StackPanel Grid.Column="1" Margin="6,0,0,0">
                                        <TextBlock Text="Remote" FontWeight="SemiBold" Margin="0,0,0,4" />
                                        <ItemsControl ItemsSource="{Binding RemoteParticipants}">
                                            <ItemsControl.ItemTemplate>
                                                <DataTemplate>
                                                    <DockPanel Margin="0,2,0,2">
                                                        <ui:Button DockPanel.Dock="Right" Content="Remove" Appearance="Secondary"
                                                                   Command="{Binding DataContext.RemoveParticipantCommand,
                                                                             RelativeSource={RelativeSource AncestorType=ItemsControl}}"
                                                                   CommandParameter="{Binding}" />
                                                        <TextBlock DockPanel.Dock="Right" Text=" (you)" Margin="4,0,0,0"
                                                                   VerticalAlignment="Center" Style="{StaticResource MutedText}"
                                                                   Visibility="{Binding IsSelf, Converter={StaticResource BoolToVis}}" />
                                                        <ui:TextBox Text="{Binding Name, Mode=OneTime}"
                                                                    PlaceholderText="{Binding DisplayLabel, Mode=OneWay}"
                                                                    ToolTip="Type a name to identify this speaker; clear it to leave it unnamed."
                                                                    LostFocus="OnParticipantNameCommitted"
                                                                    IsEnabled="{Binding DataContext.IsEditable, RelativeSource={RelativeSource AncestorType=ItemsControl}}"
                                                                    VerticalAlignment="Center" />
                                                    </DockPanel>
                                                </DataTemplate>
                                            </ItemsControl.ItemTemplate>
                                        </ItemsControl>
                                        <DockPanel Margin="0,6,0,0">
                                            <ui:Button DockPanel.Dock="Right" Content="Add" Appearance="Primary"
                                                       Command="{Binding AddRemoteNameCommand}" Margin="6,0,0,0" />
                                            <ui:TextBox PlaceholderText="Add remote speaker"
                                                        Text="{Binding NewRemoteName, UpdateSourceTrigger=PropertyChanged}" />
                                        </DockPanel>
                                        <ui:Button Content="Add unnamed speaker" Appearance="Secondary" Margin="0,6,0,0"
                                                   HorizontalAlignment="Stretch"
                                                   Command="{Binding AddRemoteUnnamedCommand}" />
                                        <DockPanel Margin="0,6,0,0">
                                            <ui:Button DockPanel.Dock="Right" Content="Add from roster" Appearance="Secondary"
                                                       Command="{Binding AddRemoteFromRosterCommand}" Margin="6,0,0,0" />
                                            <ComboBox ItemsSource="{Binding RosterPicks}" DisplayMemberPath="Display"
                                                      SelectedItem="{Binding RemoteSelectedRosterPick, Mode=TwoWay}" />
                                        </DockPanel>
                                    </StackPanel>
                                </Grid>
                            </StackPanel>
                        </ui:Card>
                    </StackPanel>
                </ScrollViewer>
            </TabItem>
        </TabControl>
    </DockPanel>
</ui:FluentWindow>
```

- [ ] **Compile + hygiene gate (this is the automated "PASS" for the XAML change).** Run the full App suite so the XAML actually compiles (the test project builds `LocalScribe.App`), and confirm `XamlHygieneTests` (the root-marker + no-hardcoded-brush asserts) still pass:
```
dotnet test "tests\LocalScribe.App.Tests\LocalScribe.App.Tests.csproj" --nologo -p:BaseOutputPath=C:\Users\SAMUE~1.SAM\AppData\Local\Temp\claude\F--LocalScribe\4c069d5c-1598-457e-ab60-c2de6a9d7c1e\scratchpad\isobin\
```
Expect: build succeeds with 0 warnings; all tests pass (in particular `XamlHygieneTests.PageAndWindowRoots_SetInheritableForeground` — the root `DockPanel` still carries `TextElement.Foreground="{DynamicResource TextFillColorPrimaryBrush}"` — and `ShippedXaml_HasNoDisallowedHardcodedBrushes` — no new `#RRGGBB` literals). A red build here means a XAML typo; fix it before proceeding.

- [ ] **Manual smoke (behavioral gate — the app is WPF).** Build/run the app (`dotnet run --project src\LocalScribe.App`, or rebuild the user's app), open a finalized session's **Session Details** (Sessions list -> Open detail), and verify:
  1. Two tabs render — **Details** and **Speakers** — with a persistent header above them showing Discard + Save (and `LockHint` empty for an editable session).
  2. **Details** tab shows Session (Title/Description), Classification (Call type + Matters search/chips/results/inline-create), and the **Archived** checkbox.
  3. **Speakers** tab shows the Split-speakers button + hint and the Local/Remote slot grid with Add / Add unnamed / Add from roster.
  4. Edit the Title -> "Unsaved changes" appears in the header and stays visible when you switch to the Speakers tab; **Save** commits (indicator clears), **Discard** reverts — both work from either tab.
  5. Toggle **Archived** in Details, Save, reopen -> the state persists.
  6. On the Speakers tab, type into a participant name box, then click the **Details** tab: focus leaves the box, `LostFocus` fires, and the rename commits (the slot keeps the typed name, no duplicate) — half-typed name is not lost on tab switch.
  7. `Ctrl+S` saves from either tab (window-scoped `KeyBinding`).
  8. Open a session that is currently **recording/pending** (or simulate a locked row): both tabs are read-only (controls disabled) but you can still switch between them; `LockHint` shows the reason and Save is greyed.

- [ ] **Commit.**
```
git add src/LocalScribe.App/SessionDetailsWindow.xaml
git commit -m "$(cat <<'EOF'
feat(app): split Session Details into Details/Speakers tabs with persistent Save header

Reorganizes SessionDetailsWindow from one long vertical scroll into a two-tab
TabControl (Details / Speakers) under a persistent Save/Discard header. Details
holds Session + Classification + the relocated Archived checkbox; Speakers holds
the slot grid. Each tab body owns its own ScrollViewer and IsEditable gates each
tab's content panel, so a live/pending session shows both tabs read-only but still
switchable. View-only change: MetadataEditorViewModel is untouched.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01X6u4L9ZxYKCuvxGBbrJrrW
EOF
)"
```

---

## Task 2: Themed Fluent unsaved-changes close prompt

**Files:**
- Modify `src/LocalScribe.App/SessionDetailsWindow.xaml.cs`:
  - `OnClosing` (current `:75-108`, including its doc comment) — rework to hand off to an async helper.
  - `SaveThenCloseAsync` (current `:110-122`, including its doc comment) — replace with `ConfirmCloseAsync`.
  - (`_closeConfirmed` field `:30` keeps its role but its comment is refreshed to name `ConfirmCloseAsync` — see the dedicated step in Task 2. `OnParticipantNameCommitted` `:124-132`, `OnClosed` `:134-142` unchanged.)
- Test (gate): full App suite build-green + `XamlHygieneTests` green + manual smoke of the four close outcomes.

**Interfaces:**
- Consumes (existing, unchanged): field `bool _closeConfirmed`; `_vm.IsDirty`, `_vm.SaveCommand` (`IAsyncRelayCommand`, `ExecuteAsync(null)`), `_vm.DiscardCommand` (`IRelayCommand`, `Execute(null)`), `_vm.RenameParticipant(ParticipantRow, string)`; `Wpf.Ui.Controls.MessageBox` + `Wpf.Ui.Controls.MessageBoxResult` (WPF-UI 4.0.3; verified API above).
- Produces: private `async System.Threading.Tasks.Task ConfirmCloseAsync()` (replaces `SaveThenCloseAsync`).

### Design decision preserved from the current code (read before editing)
The design prose says "`ConfirmCloseAsync` does the existing focused-box force-commit". This plan keeps the **focused-box force-commit in `OnClosing`, BEFORE the `IsDirty` gate** — exactly where it is today (`:86-93`). Reason: the participant-name box binds `Text` OneTime and commits only via `LostFocus`, which does NOT fire when the user closes with the X while the box is still focused (that is why the manual commit exists). If the commit moved after the `IsDirty` check, a session whose ONLY pending change is a half-typed rename would read `IsDirty == false`, skip the prompt, and silently lose the rename. Committing first preserves the current data-safety guarantee. Only the **dialog** becomes async/themed.

### Steps

- [ ] **Baseline green.** (Same suite as Task 1 — confirm still green after Task 1's commit.)
```
dotnet test "tests\LocalScribe.App.Tests\LocalScribe.App.Tests.csproj" --nologo -p:BaseOutputPath=C:\Users\SAMUE~1.SAM\AppData\Local\Temp\claude\F--LocalScribe\4c069d5c-1598-457e-ab60-c2de6a9d7c1e\scratchpad\isobin\
```
Expect: all pass.

- [ ] **Rework `OnClosing`** to cancel the close on a dirty editor and hand off to the async helper. Replace the current doc comment + method (`:75-108`):
  - old_string (exact current text):
```csharp
    /// <summary>Stage 5.4 5.1 close guard: a dirty editor prompts Save / Discard / Cancel
    /// (Yes/No/Cancel), fixing the "title typed then X" data-loss path. WPF cannot await inside
    /// OnClosing, so Save CANCELS this close, awaits the commit, then re-Closes with
    /// _closeConfirmed set. Mirrors MattersPage.OnDeleteMatter's MessageBox confirm pattern.</summary>
    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (_closeConfirmed) return;
        // Force-commit a focused LostFocus-bound TextBox, if any, so IsDirty and the VM working
        // copy reflect what is on screen before we decide anything (belt-and-braces: the current
        // fields all bind PropertyChanged, but this stays safe for any future LostFocus binding).
        if (Keyboard.FocusedElement is TextBox tb)
        {
            // A participant name box binds Text OneTime and commits via LostFocus->RenameParticipant,
            // which never fires if the user types then closes with X while still focused. Commit it
            // here so the rename (and its dirty flag) is captured before the save/discard decision.
            if (tb.DataContext is ParticipantRow row) _vm.RenameParticipant(row, tb.Text);
            else tb.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
        }
        if (!_vm.IsDirty) return;

        var choice = MessageBox.Show(
            "Save changes to this session before closing?\n\nYes saves, No discards the changes, Cancel keeps editing.",
            "Unsaved changes", MessageBoxButton.YesNoCancel, MessageBoxImage.Warning,
            MessageBoxResult.Cancel);
        if (choice == MessageBoxResult.Cancel) { e.Cancel = true; return; }
        if (choice == MessageBoxResult.No)
        {
            _vm.DiscardCommand.Execute(null);   // revert, then let the close proceed
            return;
        }
        e.Cancel = true;                        // Yes: the commit is async - stop THIS close
        _ = SaveThenCloseAsync();
    }
```
  - new_string:
```csharp
    /// <summary>Close guard: a dirty editor prompts Save / Discard / Cancel via a themed Fluent
    /// dialog. WPF cannot await inside OnClosing, so a dirty editor CANCELS this close and hands
    /// off to ConfirmCloseAsync, which shows the dialog and re-Closes (with _closeConfirmed set)
    /// only on Save-that-settled-clean or Discard. The focused-box force-commit stays HERE, before
    /// the IsDirty gate: a participant name box commits only via LostFocus, which never fires on an
    /// X-close, so committing after the gate could drop a half-typed rename that is the only edit.</summary>
    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (_closeConfirmed) return;
        // Force-commit a focused LostFocus-bound TextBox, if any, so IsDirty and the VM working
        // copy reflect what is on screen before we decide anything.
        if (Keyboard.FocusedElement is TextBox tb)
        {
            // A participant name box binds Text OneTime and commits via LostFocus->RenameParticipant,
            // which never fires if the user types then closes with X while still focused. Commit it
            // here so the rename (and its dirty flag) is captured before the save/discard decision.
            if (tb.DataContext is ParticipantRow row) _vm.RenameParticipant(row, tb.Text);
            else tb.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
        }
        if (!_vm.IsDirty) return;               // clean: let the close proceed
        e.Cancel = true;                        // dirty: stop THIS close; decide via the async dialog
        _ = ConfirmCloseAsync();
    }
```

- [ ] **Replace `SaveThenCloseAsync` with `ConfirmCloseAsync`.** Replace the current doc comment + method (`:110-122`):
  - old_string (exact current text):
```csharp
    /// <summary>Awaits the explicit save, then closes only if it actually settled clean.
    /// Ordering is safe: SaveAsync posts its completion (IsDirty/Saved) via Dispatcher
    /// BeginInvoke BEFORE its task completes, and this await's own dispatcher continuation is
    /// queued after task completion, so IsDirty here reliably reflects the outcome. A failed
    /// save (error already reported) or a declined attribution warning leaves the editor dirty
    /// and the window OPEN. SaveAsync catches all exceptions, so the discard is safe.</summary>
    private async System.Threading.Tasks.Task SaveThenCloseAsync()
    {
        await _vm.SaveCommand.ExecuteAsync(null);
        if (_vm.IsDirty) return;                // save failed or was declined - stay open
        _closeConfirmed = true;
        Close();
    }
```
  - new_string:
```csharp
    /// <summary>Themed unsaved-changes prompt (WPF-UI 4.0.3 Wpf.Ui.Controls.MessageBox). OnClosing
    /// already cancelled the close and force-committed a focused rename; here we show the Fluent
    /// Save / Discard / Cancel dialog and act on the choice. Primary (Save) awaits the explicit
    /// commit and re-Closes only if it settled clean - a failed or declined save leaves the editor
    /// dirty and the window OPEN (unchanged semantics; SaveAsync catches its own exceptions).
    /// Secondary (Discard) reverts and closes. None (Cancel / Esc / title-bar close) stays open.
    /// The dialog is shown on a user close action, long after the message pump is up, so the Wpf.Ui
    /// Mica-window-before-pump rendering gotcha does not apply.</summary>
    private async System.Threading.Tasks.Task ConfirmCloseAsync()
    {
        var dialog = new Wpf.Ui.Controls.MessageBox
        {
            Owner = this,
            Title = "Unsaved changes",
            Content = "Save changes to this session before closing?",
            PrimaryButtonText = "Save",
            SecondaryButtonText = "Discard",
            CloseButtonText = "Cancel",
        };
        switch (await dialog.ShowDialogAsync())
        {
            case Wpf.Ui.Controls.MessageBoxResult.Primary:      // Save
                await _vm.SaveCommand.ExecuteAsync(null);
                if (_vm.IsDirty) return;                        // save failed or was declined - stay open
                _closeConfirmed = true;
                Close();
                break;
            case Wpf.Ui.Controls.MessageBoxResult.Secondary:    // Discard
                _vm.DiscardCommand.Execute(null);               // revert
                _closeConfirmed = true;
                Close();
                break;
            // MessageBoxResult.None (Cancel / Esc / title-bar close): keep editing - do nothing.
        }
    }
```

- [ ] **Refresh the now-stale `_closeConfirmed` field comment** (Task 2 deletes `SaveThenCloseAsync`, but `SessionDetailsWindow.xaml.cs:30` still names it, leaving a dangling reference). Change the field comment from `// set by SaveThenCloseAsync so the re-entrant Close skips the prompt` to `// set by ConfirmCloseAsync (Save-clean or Discard) so the re-entrant Close skips the prompt`.

- [ ] **Compile + suite gate (automated "PASS").** The dialog logic needs an STA WPF window + user interaction, so it is not headless-unit-testable; the automated gate is a clean 0-warning build and the untouched suite staying green (no VM change).
```
dotnet test "tests\LocalScribe.App.Tests\LocalScribe.App.Tests.csproj" --nologo -p:BaseOutputPath=C:\Users\SAMUE~1.SAM\AppData\Local\Temp\claude\F--LocalScribe\4c069d5c-1598-457e-ab60-c2de6a9d7c1e\scratchpad\isobin\
```
Expect: build succeeds with 0 warnings (in particular no CS-warning from the removed `System.Windows.MessageBox` usage — `System.Windows` is still used by `Window`/`RoutedEventArgs`/`SystemParameters`, so no unused-using); all tests pass, including every `MetadataEditor*` test (confirming the VM is untouched) and `XamlHygieneTests`.

- [ ] **Manual smoke (behavioral gate).** Run the app, open a session's **Session Details**, make an edit (so `IsDirty`), then click the window's X and verify the **themed** (Fluent, Mica-styled) dialog titled "Unsaved changes" with buttons **Save / Discard / Cancel**:
  1. **Save** -> commits, then the window closes; if the save fails or the attribution warning is declined, the editor stays dirty and OPEN.
  2. **Discard** -> window closes, edits lost.
  3. **Cancel** (and pressing **Esc** / the dialog's own X) -> dialog dismisses, Session Details stays open with edits intact.
  4. With NO unsaved edits, clicking X closes immediately with no prompt.
  5. Repeat 1-3 while a participant name box is focused with a half-typed name (X-close): the rename is committed first, so it participates in the Save/Discard decision (not silently lost).

- [ ] **Commit.**
```
git add src/LocalScribe.App/SessionDetailsWindow.xaml.cs
git commit -m "$(cat <<'EOF'
feat(app): themed Fluent unsaved-changes close prompt for Session Details

Replaces the plain System.Windows.MessageBox close guard with a themed
Wpf.Ui.Controls.MessageBox (Save / Discard / Cancel). Because the themed dialog is
async and WPF cannot await inside OnClosing, OnClosing now cancels a dirty close and
hands off to ConfirmCloseAsync (Primary=Save-then-close-if-clean, Secondary=Discard-
then-close, None=stay open). The focused-box force-commit stays in OnClosing before
the IsDirty gate so a half-typed rename is never lost on an X-close. No VM change.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01X6u4L9ZxYKCuvxGBbrJrrW
EOF
)"
```

---

## Self-review (writing-plans)

### (a) Spec coverage — every spec section maps to a task
| Spec requirement | Task |
| --- | --- |
| View-only reorganization; no `MetadataEditorViewModel` change | Tasks 1 + 2 (verified by `MetadataEditor*` tests staying green) |
| Persistent header (Dock Top, outside tabs): `LockHint` left; Unsaved-changes + Discard + Save right | Task 1 (header `DockPanel`) |
| Drop the in-content "Details" heading | Task 1 (removed) |
| `TabControl` with two `TabItem`s: **Details** / **Speakers** | Task 1 |
| Details tab = Session card + Classification card + relocated **Archived** checkbox | Task 1 |
| Speakers tab = Speakers card (Split button, `DiariseHint`, Local/Remote grid, add controls) | Task 1 |
| Each tab body wrapped in its own `ScrollViewer` | Task 1 |
| `IsEditable` gating on each tab's content panel (not the `TabControl`) | Task 1 (`IsEnabled` on each `StackPanel`) |
| Tab labels "Details" / "Speakers" | Task 1 |
| Chip style + matter controls move verbatim into Details | Task 1 |
| Keyboard save (`Ctrl+S`) window-scoped, unaffected | Task 1 (`KeyBinding` retained) |
| Pending renames commit on tab switch via `LostFocus` | Task 1 (smoke step 6; handler unchanged) |
| Replace `System.Windows.MessageBox.Show` with themed `Wpf.Ui.Controls.MessageBox` (Title/Content/Primary=Save/Secondary=Discard/Close=Cancel, `ShowDialogAsync`) | Task 2 |
| Fold decision into async `ConfirmCloseAsync`; OnClosing sets `e.Cancel` on dirty and calls it; `_closeConfirmed` re-entrant pattern | Task 2 |
| Save-fails/declined stays dirty+open; Discard closes; Cancel/None stays open | Task 2 (switch + smoke) |
| Existing `MetadataEditorViewModel` unit tests remain green | Tasks 1 + 2 gate |
| `XamlHygieneTests` still pass (root marker, no new app-global implicit styles) | Tasks 1 + 2 gate |
| Out of scope: edit-model / matters / diarisation logic; 1-on-1 gating papercut | Not implemented (correct) |

All spec sections map to a task. **No unmapped requirement.**

### (b) Placeholder scan
No `TBD`, no "similar to Task N", no "add error handling". Both tasks show the complete real XAML/C# (full window file in Task 1; exact old_string/new_string edits in Task 2), all grounded in the files as read. The Wpf.Ui API (`Title`, `Content`, `PrimaryButtonText`, `SecondaryButtonText`, `CloseButtonText`, `ShowDialogAsync` -> `MessageBoxResult{None,Primary,Secondary}`) was verified by reflection against the actual 4.0.3 assembly, not invented.

### (c) Type consistency across tasks
- `ConfirmCloseAsync` (produced by Task 2) is the only new member and is referenced only by `OnClosing` in the same task; `SaveThenCloseAsync` (its predecessor) is removed in the same edit, and its one remaining textual mention — the `_closeConfirmed` field comment at `:30` — is refreshed by the dedicated step above.
- Every binding path in Task 1's XAML resolves to a real existing `MetadataEditorViewModel` member (enumerated under "Verified facts"); none are introduced or renamed.
- `Wpf.Ui.Controls.MessageBox` / `MessageBoxResult` are fully qualified in code-behind to avoid clashing with `System.Windows.MessageBox`/`MessageBoxResult` reachable via the existing `using System.Windows;` — no new `using` is added, so no ambiguity and no unused-using warning.
- Root-container marker `TextElement.Foreground="{DynamicResource TextFillColorPrimaryBrush}"` stays on the top-level `DockPanel`, satisfying `XamlHygieneTests.PageAndWindowRoots_SetInheritableForeground` after the rewrite.
