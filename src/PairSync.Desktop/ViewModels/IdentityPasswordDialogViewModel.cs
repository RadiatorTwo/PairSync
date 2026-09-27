using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PairSync.Desktop.Resources;
using PairSync.Storage.Identity;

namespace PairSync.Desktop.ViewModels;

/// <summary>
/// Asks for the password of an identity backup: twice when exporting, once when importing. <c>check</c> tries the
/// password (import) and returns an error to show, or null to close with it.
/// </summary>
public sealed partial class IdentityPasswordDialogViewModel(bool export, Func<string, string?>? check = null) : DialogViewModel
{
    [ObservableProperty]
    private string _password = "";

    [ObservableProperty]
    private string _repeat = "";

    [ObservableProperty]
    private string? _error;

    public bool IsExport { get; } = export;

    public string Title => IsExport ? Strings.Identity_PasswordTitleExport : Strings.Identity_PasswordTitleImport;

    /// <summary>The accepted password; null if the user canceled.</summary>
    public string? Result { get; private set; }

    [RelayCommand]
    private void Confirm()
    {
        if (IsExport && Password.Length < IdentityBackup.MinPasswordLength)
            Error = string.Format(CultureInfo.CurrentCulture, Strings.Identity_TooShort, IdentityBackup.MinPasswordLength);
        else if (IsExport && Password != Repeat)
            Error = Strings.Identity_Mismatch;
        else if (check?.Invoke(Password) is { } problem)
            Error = problem;
        else
        {
            Result = Password;
            Close();
        }
    }

    [RelayCommand]
    private void Cancel() => Close();
}
