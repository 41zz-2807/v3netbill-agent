using System;
using System.Globalization;
using System.Linq;

namespace V3Netbill.Agent.Core;

/// <summary>
/// Hasil pemeriksaan versi — kembaran C# dari payload <c>cek-pembaruan</c>.
/// </summary>
public sealed class InfoPembaruan
{
    /// <summary>Sakelar auto-update di halaman Pengaturan.</summary>
    public bool AutoUpdate { get; init; }

    /// <summary>Ada MSI di server (bukan berarti harus dipasang).</summary>
    public bool Ada { get; init; }

    /// <summary>Versi MSI di server, mis. "1.0.26.0". Null kalau tidak terbaca.</summary>
    public string? Versi { get; init; }

    /// <summary>SHA-256 berkas MSI. Wajib cocok sebelum dipasang.</summary>
    public string? Sha256 { get; init; }

    public long? UkuranBytes { get; init; }
}

/// <summary>Keputusan: apa yang harus dilakukan agent.</summary>
public enum KeputusanUpdate
{
    /// <summary>Tidak ada yang perlu dikerjakan.</summary>
    Diam,

    /// <summary>Ada versi lebih baru dan semua prasyarat aman → unduh lalu pasang.</summary>
    PerluUpdate,

    /// <summary>Server bilang ada update, tapi sakelarnya mati.</summary>
    SakelarMati,

    /// <summary>Versi server tidak bisa dibaca → tidak boleh menebak.</summary>
    VersiTidakSah,
}

/// <summary>
/// Logika pembaruan otomatis agent (self-update).
/// </summary>
/// <remarks>
/// <para>
/// <b>Kenapa ini dipisah dari I/O.</b> Semua keputusan ada di sini, murni
/// tanpa jaringan, tanpa registry, tanpa proses. Itu yang membuatnya bisa
/// diuji di Linux (proyek ini <c>net8.0</c>, bukan <c>net8.0-windows</c>) —
/// termasuk kasus-kasus yang di PC sungguhan akan berisiko memutus penagihan.
/// </para>
///
/// <para>
/// <b>Aturan utama: agent tidak pernah dipaksa server.</b> Server cuma berkata
/// "sakelarnya nyala dan ada MSI versi begini". Yang tahu versi dirinya sendiri,
/// yang tahu sedang ada sesi berjalan atau tidak, dan yang tahu kalau teknisi
/// sedang maintenance — semuanya di PC ini. Kalau keputusan install ada di
/// server, satu MSI rusak bisa menjatuhkan billing di semua PC sekaligus.
/// </para>
/// </remarks>
public static class AutoUpdater
{
    /// <summary>Satu perintah install berukuran puluhan MB; jangan macet lama.</summary>
    public const int TimeoutUnduhDetik = 15 * 60;

    /// <summary>msiexec butuh waktu; major upgrade menghentikan service dulu.</summary>
    public const int TimeoutPasangDetik = 10 * 60;

    /// <summary>Jarak minimum antar percobaan, supaya gagal berulang tidak membanjiri server.</summary>
    public static readonly TimeSpan JedaPercobaan = TimeSpan.FromHours(1);

    /// <summary>
    /// Berapa kali percobaan boleh gagal sebelum agent berhenti sendiri.
    /// </summary>
    /// <remarks>
    /// Dua kali, bukan "coba terus". Mati listrik atau restart manual di tengah
    /// unduhan tidak boleh membuat PC kembali normal lalu menyalakan update
    /// berulang setiap 6 jam — itu yang membuat MSI rusak terasa seperti
    /// "PC nyala sendiri terus".
    ///
    /// Setelah batas ini, flag di server TIDAK dilepas sendiri. Operator tetap
    /// memegang kendali: PC tidak bisa dilogin, dan itu lebih baik daripada
    /// update berulang yang membanjiri server tanpa pernah berhasil.
    /// </remarks>
    public const int MaksPercobaanGagal = 2;

    /// <summary>
    /// Hitung mundur sebelum restart paksa.
    /// </summary>
    /// <remarks>
    /// ⚠️ Pasang tanpa jeda sama sekali membuat pelanggan kehilangan berkasnya
    /// tanpa sempat menyimpan. Sepuluh detik cukup untuk menjangkau tombol simpan.
    /// </remarks>
    public const int HitungMundurRestartDetik = 10;

    /// <summary>
    /// Putuskan apa yang harus dilakukan berdasarkan balasan server + kondisi PC.
    /// </summary>
    /// <param name="versiLokal">Versi MSI yang terpasang di PC ini.</param>
    /// <param name="info">Balasan server.</param>
    /// <param name="adaSesiBerjalan">Ada pelanggan sedang main.</param>
    /// <param name="maintenance">Teknisi sedang memperbaiki PC ini.</param>
    /// <param name="pcTerkunci">
    /// Layar sedang terkunci. Wajib: ini satu-satunya syarat yang menegaskan "tidak ada
    /// orang yang sedang memakai PC".
    /// </param>
    public static KeputusanUpdate Putuskan(
        string? versiLokal,
        InfoPembaruan? info,
        bool adaSesiBerjalan,
        bool maintenance,
        bool pcTerkunci)
    {
        // ⚠️ Penjaga terpenting, dan WAJIB paling atas. PC yang sedang dipakai
        // pelanggan tidak boleh disengketakan oleh instalasi: msiexec akan
        // menghentikan service, dan sesi yang sedang berjalan berhenti ditagih.
        // Konsekuensinya pelanggan bisa main lebih lama dari yang dibayar.
        if (adaSesiBerjalan) return KeputusanUpdate.Diam;

        // Layar terbuka tanpa sesi = ada orang di depan PC (mis. baru saja
        // selesai login, atau teknisi yang tidak men-set flag maintenance).
        // Installer yang jalan saat itu menutup program yang sedang dikerjakan.
        if (!pcTerkunci) return KeputusanUpdate.Diam;

        // Mode maintenance berarti PC sengaja dibuka untuk diperbaiki.
        if (maintenance) return KeputusanUpdate.Diam;

        if (info == null || !info.AutoUpdate) return KeputusanUpdate.Diam;
        if (!info.Ada) return KeputusanUpdate.Diam;

        var server = ParseVersi(info.Versi);
        var lokal = ParseVersi(versiLokal);
        if (server == null) return KeputusanUpdate.VersiTidakSah;
        if (lokal == null) return KeputusanUpdate.VersiTidakSah;

        return Bandingkan(server, lokal) > 0
            ? KeputusanUpdate.PerluUpdate
            : KeputusanUpdate.Diam;
    }

    /// <summary>True kalau kedua versi sama.</summary>
    public static bool SamaVersi(string? a, string? b)
    {
        var pa = ParseVersi(a);
        var pb = ParseVersi(b);
        if (pa == null || pb == null) return false;
        return Bandingkan(pa, pb) == 0;
    }

    /// <summary>
    /// Bandingkan dua versi numerik per bagian.
    /// Mengembalikan negatif kalau <paramref name="a"/> &lt; <paramref name="b"/>,
    /// nol kalau sama, positif kalau <paramref name="a"/> &gt; <paramref name="b"/>.
    /// </summary>
    /// <remarks>
    /// ⚠️ JANGAN pakai <c>string.Compare</c> atau <c>&lt;</c> pada string versi.
    /// Itu perbandingan leksikografis, bukan numerik: "1.0.10.0" akan dianggap
    /// LEBIH KECIL dari "1.0.9.0" karena karakter "1" datang sebelum "9" —
    /// sehingga upgrade tidak pernah terjadi, dan itulah jenis bug yang paling
    /// sulit ketahuan: aplikasi terlihat "sudah terbaru" padahal ada patch.
    /// </remarks>
    public static int Bandingkan(int[] a, int[] b)
    {
        // Semua versi dinormalkan ke 4 bagian (lihat ParseVersi), jadi panjang
        // keduanya sudah sama dan tidak bisa beda di sini.
        var n = Math.Max(a.Length, b.Length);
        for (var i = 0; i < n; i++)
        {
            var x = i < a.Length ? a[i] : 0;
            var y = i < b.Length ? b[i] : 0;
            if (x != y) return x.CompareTo(y);
        }
        return 0;
    }

    /// <summary>
    /// Apakah agent masih boleh mencoba lagi setelah beberapa kegagalan.
    /// </summary>
    /// <param name="gagalBeruntun">Berapa kali percobaan terakhir gagal.</param>
    /// <param name="sejakPercobaanTerakhir">Selisih waktu sejak percobaan terakhir.</param>
    /// <remarks>
    /// Fungsi murni supaya batas dua percobaan bisa diuji tanpa registry, tanpa
    /// timer, dan tanpa menunggu satu jam.
    /// </remarks>
    public static bool MasihBolehPercobaan(int gagalBeruntun, TimeSpan sejakPercobaanTerakhir)
    {
        if (gagalBeruntun <= 0) return true;
        if (gagalBeruntun >= MaksPercobaanGagal) return false;
        return sejakPercobaanTerakhir >= JedaPercobaan;
    }

    /// <summary>
    /// Pesan yang harus tampil ke pengguna, sesuai tahap yang sedang berjalan.
    /// </summary>
    /// <remarks>
    /// ⚠️ Satu fungsi untuk semua tahap, karena tiap tahap memakai kalimat
    /// berbeda, dan menyalin rumus ini ke beberapa tempat sudah pernah
    /// menghasilkan dua layar yang berbeda untuk keadaan yang sama.
    /// Persentase DIBATAS 0..100 — nilai server bisa saja lebih besar kalau
    /// ada bug di sana, dan angka 130% di layar operator terlihat seperti
    /// file rusak.
    /// </remarks>
    public static (int Persen, string Pesan) TahapProgress(string tahap, double? rasioUnduhan)
    {
        // Unduhan menempati 70% pertama karena itu satu-satunya tahap yang
        // progress-nya bisa benar-benar diukur. Hashing dan msiexec tidak
        // melaporkan progress, jadi mengestimasinya berarti menampilkan angka
        // yang tidak berasal dari mana pun.
        var persen = tahap switch
        {
            "unduh" => RasioKePersen(rasioUnduhan, 5, 70),
            "verifikasi" => 75,
            "pasang" => 90,
            "selesai" => 100,
            "gagal" => 100,
            _ => 0,
        };

        var pesan = tahap switch
        {
            "unduh" => "Mengunduh pembaruan...",
            "verifikasi" => "Memeriksa keutuhan berkas...",
            "pasang" => "Memasang pembaruan. Jangan matikan PC ini.",
            "selesai" => "Pembaruan selesai. PC akan restart sebentar lagi.",
            "gagal" => "Pembaruan gagal. PC tidak direstart, hubungi teknisi.",
            _ => "Menyiapkan pembaruan...",
        };

        return (Math.Clamp(persen, 0, 100), pesan);
    }

    /// <summary>Naikkan rasio 0..1 menjadi rentang persen tertentu.</summary>
    private static int RasioKePersen(double? rasio, int dari, int sampai)
    {
        if (rasio is null || double.IsNaN(rasio.Value)) return dari;
        var b = Math.Clamp(rasio.Value, 0d, 1d);

        // ⚠️ MidpointRounding.AwayFromZero itu WAJIB, bukan bawaan.
        //
        // Math.Round(32.5) bawaan .NET memakai banker's rounding dan
        // menghasilkan 32, bukan 33. Untuk progress bar itu berarti angka
        // melompat melingkar di titik tengah dan-naik-turun di detik yang
        // sama — persis gerakan yang membuat orang mengira program macet.
        return dari + (int)Math.Round((sampai - dari) * b, MidpointRounding.AwayFromZero);
    }

    /// <summary>
    /// Ubah "1.0.26.0" jadi <c>[1, 0, 26, 0]</c>.
    /// </summary>
    /// <returns>
    /// Empat bagian, atau <c>null</c> kalau bentuknya tidak sah.
    /// </returns>
    /// <remarks>
    /// Bagian yang tidak ditulis dianggap 0, jadi "1.0" == "1.0.0.0". Tanda
    /// minus, huruf, spasi, dan bagian kosong ditolak — versi yang tidak sah
    /// menghasilkan <c>null</c> dan pemanggil BERHENTI, tidak menebak. Menebak
    /// berarti bisa memasang versi lain dari yang direncanakan.
    /// </remarks>
    public static int[]? ParseVersi(string? versi)
    {
        if (string.IsNullOrWhiteSpace(versi)) return null;

        var teks = versi.Trim();
        var potongan = teks.Split('.');

        if (potongan.Length is < 1 or > 4) return null;

        var hasil = new int[4];
        for (var i = 0; i < potongan.Length; i++)
        {
            var bagian = potongan[i];
            if (bagian.Length == 0) return null;

            // Hanya digit murni. int.TryParse dengan NumberStyles.None sudah
            // menolak tanda +, -, spasi, dan notasi ilmiah.
            if (!int.TryParse(bagian, NumberStyles.None, CultureInfo.InvariantCulture, out var angka))
            {
                return null;
            }
            if (angka < 0) return null;
            hasil[i] = angka;
        }

        return hasil;
    }
}
