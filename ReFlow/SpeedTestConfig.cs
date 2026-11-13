namespace ReFlow;

/// <summary>
/// URL для замера скорости задаётся только здесь (не в файле настроек).
/// Укажите прямой HTTPS-URL крупного статического файла на вашем хостинге.
/// </summary>
public static class SpeedTestConfig
{
    public const string DownloadFileUrl = "https://nerabota.ru/speedtest.bin";

    /// <summary>Название основного сервера — видно в подсказке циферблата.</summary>
    public static readonly (string Ru, string En) DownloadFileName = ("Основной (Россия)", "Main (Russia)");

    /// <summary>Название для своего файла из настроек.</summary>
    public static readonly (string Ru, string En) CustomFileName = ("Свой файл", "Custom file");

    /// <summary>
    /// Запасные файлы по порядку — если основной сервер (или свой из настроек) не отвечает. Переключение незаметное,
    /// название сервера видно только в подсказке циферблата.
    /// GitHub: релиз «Файл для замера» (300 МБ); Яндекс и PS.kz: замороженный образ Ubuntu 14.04 (≈1,1 ГБ);
    /// OVH, Tele2: тестовые файлы по 1 ГБ.
    /// </summary>
    public static readonly ((string Ru, string En) Name, string Url)[] FallbackDownloadFiles =
    {
        (("GitHub (Великобритания)", "GitHub (United Kingdom)"), "https://github.com/dinosmike/ReFlow/releases/download/speedtest-file/speedtest.bin"),
        (("Яндекс (Россия)", "Yandex (Russia)"), "https://mirror.yandex.ru/ubuntu-releases/trusty/ubuntu-14.04.6-desktop-amd64.iso"),
        (("PS.kz (Казахстан)", "PS.kz (Kazakhstan)"), "https://mirror.ps.kz/ubuntu-releases/14.04.6/ubuntu-14.04.6-desktop-amd64.iso"),
        (("OVH (Франция)", "OVH (France)"), "https://proof.ovh.net/files/1Gb.dat"),
        (("Tele2 (Швеция)", "Tele2 (Sweden)"), "http://speedtest.tele2.net/1GB.zip"),
    };
}
