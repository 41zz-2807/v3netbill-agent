using System;
using System.Windows;

namespace V3Netbill.Agent.Overlay;

/// <summary>
/// Dialog ganti password, dipanggil dari mini window saat sesi berjalan.
/// Sengaja jendela terpisah supaya tidak pernah ikut terpotong oleh ukuran
/// mini window (340x268).
/// </summary>
public partial class BuatPasswordDialogWindow : Window
{
    /// <summary>Kode akun yang sedang dipakai sesi ini.</summary>
    public string Kode { get; init; } = string.Empty;

    /// <summary>
    /// Dipanggil saat tombol GANTI ditekan. Parameternya: kode, password lama,
    /// password baru. Pemanggil yang mengurus pengiriman ke server lewat pipe.
    /// </summary>
    public event Action<string, string, string>? Submit;

    private bool _sedangKirim;

    public BuatPasswordDialogWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => PasswordLamaBox.Focus();
    }

    public string PasswordLama => PasswordLamaBox.Password;
    public string PasswordBaru => PasswordBaruBox.Password;
    public string PasswordUlangi => PasswordUlangiBox.Password;

    /// <summary>Tampilkan pesan galat di dalam dialog.</summary>
    public void TampilkanGalat(string pesan)
    {
        ErrorText.Text = pesan;
        ErrorText.Visibility = Visibility.Visible;
    }

    /// <summary>
    /// Aktifkan lagi tombol GANTI setelah jawaban server datang, atau tutup
    /// dialog kalau permintaannya berhasil.
    /// </summary>
    public void SelesaiKirim(bool sukses)
    {
        _sedangKirim = false;
        OkButton.IsEnabled = true;

        if (sukses) Close();
    }

    private void OkButton_Click(object sender, RoutedEventArgs e)
    {
        // Jangan sampai dobel-kirim kalau server lambat dan tombol ditekan
        // berkali-kali.
        if (_sedangKirim) return;
        _sedangKirim = true;
        OkButton.IsEnabled = false;
        TampilkanGalat("Mengirim ke server...");
        Submit?.Invoke(Kode, PasswordLama, PasswordBaru);
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
