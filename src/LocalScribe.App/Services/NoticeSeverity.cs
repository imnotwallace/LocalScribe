namespace LocalScribe.App.Services;

/// <summary>How a queued shell notice renders in MainWindow's InfoBar (Tier 1 plan D, T1-5,
/// 2026-08-05). Mirrors Wpf.Ui.Controls.InfoBarSeverity's four members BY NAME but is declared
/// here rather than reusing that type: IUiErrorReporter and InfoBarErrorReporter are WPF-free by
/// design (see InfoBarErrorReporter's own doc comment), and taking a Wpf.Ui type into that seam
/// would drag WPF into all 24 test fakes that implement the interface. MainWindow maps this to
/// the control enum at the ONE place it renders - SyncInfoBar.
/// REJECTED: a bool isError. The bar has four states, and the defect being fixed is precisely
/// that a two-state model (hardcoded Error, never re-set) painted every success red.</summary>
public enum NoticeSeverity { Informational, Success, Warning, Error }
