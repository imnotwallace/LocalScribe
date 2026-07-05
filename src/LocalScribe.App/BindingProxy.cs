using System.Windows;

namespace LocalScribe.App;

/// <summary>Freezable relay that lets a control whose runtime DataContext differs from the page's
/// (here: a row-level <c>ContextMenu</c> on the Sessions grid, whose DataContext is the row VM)
/// reach the page VM's commands. Declared once in a page's resources as
/// <c>&lt;local:BindingProxy x:Key="VmProxy" Data="{Binding}" /&gt;</c>: a Freezable supplies its
/// owning resource dictionary an inheritance context, so <see cref="Data"/>'s <c>{Binding}</c>
/// resolves against the owning element's DataContext (the page VM). Consumers then bind
/// <c>Command="{Binding Data.SomeCommand, Source={StaticResource VmProxy}}"</c> while
/// <c>CommandParameter="{Binding}"</c> stays the local (row) DataContext.</summary>
public sealed class BindingProxy : Freezable
{
    public static readonly DependencyProperty DataProperty =
        DependencyProperty.Register(nameof(Data), typeof(object), typeof(BindingProxy),
            new PropertyMetadata(null));

    public object? Data
    {
        get => GetValue(DataProperty);
        set => SetValue(DataProperty, value);
    }

    protected override Freezable CreateInstanceCore() => new BindingProxy();
}
