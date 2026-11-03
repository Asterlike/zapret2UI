using Zapret2UI.Services.Engine;

namespace Zapret2UI.Services.Strategies;

/// <summary>
/// The global <c>--blob=</c> declarations every combo-shaped strategy carries — the built-ins
/// (<see cref="PresetService.BuildComboArgs"/>), the auto-select candidates (<see cref="ComboStrategyCatalog"/>)
/// and through them everything the generator assembles. One list, because since zapret2 v1.0.4 a blob that
/// is used but not declared silently stops the whole execution plan: two copies of this list drifting apart
/// is exactly how a strategy ends up green in the tests of one builder and dead in the other. Declaring a
/// blob nobody uses costs nothing.
/// </summary>
internal static class StrategyBlobs
{
    internal static IEnumerable<string> Declarations => Engine.Concat(FlowsealBlobs.Declarations);

    /// <summary>Blobs that ship with the zapret2 release, under <c>files\fake\</c>.</summary>
    private static readonly string[] Engine =
    {
        "--blob=tls_google:@{FILES}\\fake\\tls_clienthello_www_google_com.bin",
        "--blob=quic_google:@{FILES}\\fake\\quic_initial_www_google_com.bin",
        // Отечественные фейк-блобы (ClientHello vk.com / sberbank.ru / gosuslugi.ru) — ТСПУ вайтлистит
        // домашний трафик, поэтому фейк «под vk/сбер» выживает там, где google-фейк режется.
        "--blob=tls_vk:@{FILES}\\fake\\tls_clienthello_vk_com.bin",
        "--blob=tls_sber:@{FILES}\\fake\\tls_clienthello_sberbank_ru.bin",
        "--blob=tls_gos:@{FILES}\\fake\\tls_clienthello_gosuslugi_ru.bin",
        // Отечественный QUIC-блоб для голоса Discord (аналог dbankcloud_ru из Flowseal ALT10/11).
        "--blob=quic_vk:@{FILES}\\fake\\quic_initial_vk_com.bin",
        // STUN-пакет движка: Flowseal ALT12 шлёт его как TLS-фейк.
        "--blob=stun:@{FILES}\\fake\\stun.bin",
    };
}
