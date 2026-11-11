using Microsoft.UI.Xaml;
using Windows.Storage.Pickers;

namespace NeoRar.Services;

/// <summary>Boîtes « Ouvrir », « Enregistrer sous » et « Choisir un dossier » de Windows, rattachées à une fenêtre.</summary>
public static class Pickers
{
    public static async Task<string?> PickArchiveAsync(Window owner)
    {
        var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.Downloads, ViewMode = PickerViewMode.List };
        foreach (var ext in ArchiveService.SupportedExtensions)
            picker.FileTypeFilter.Add(ext);
        picker.FileTypeFilter.Add("*");
        Attach(picker, owner);
        return (await picker.PickSingleFileAsync())?.Path;
    }

    public static async Task<IReadOnlyList<string>> PickFilesAsync(Window owner)
    {
        var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary, ViewMode = PickerViewMode.List };
        picker.FileTypeFilter.Add("*");
        Attach(picker, owner);
        var files = await picker.PickMultipleFilesAsync();
        return files.Select(f => f.Path).ToList();
    }

    public static async Task<string?> PickFolderAsync(Window owner)
    {
        var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.Desktop };
        picker.FileTypeFilter.Add("*");
        Attach(picker, owner);
        return (await picker.PickSingleFolderAsync())?.Path;
    }

    public static async Task<string?> PickSaveArchiveAsync(Window owner, string suggestedName, ArchiveKind kind)
    {
        var picker = new FileSavePicker { SuggestedStartLocation = PickerLocationId.Desktop, SuggestedFileName = Path.GetFileNameWithoutExtension(suggestedName) };
        picker.FileTypeChoices.Add(kind switch
        {
            ArchiveKind.Zip => "Archive ZIP",
            ArchiveKind.Tar => "Archive TAR",
            _ => "Archive 7z",
        }, [ArchiveService.Extension(kind)]);
        Attach(picker, owner);
        return (await picker.PickSaveFileAsync())?.Path;
    }

    private static void Attach(object picker, Window owner) =>
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(owner));
}
