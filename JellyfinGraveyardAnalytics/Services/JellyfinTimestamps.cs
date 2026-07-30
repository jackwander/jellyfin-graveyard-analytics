using System;

namespace JellyfinGraveyardAnalytics.Services
{
    /// <summary>
    /// Normalizes the <see cref="DateTime"/> values this plugin reads off Jellyfin's own
    /// entities, so that everything downstream — comparisons and the wire alike — is UTC.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The third helper of its kind, alongside <c>Repository.TryParseStoredUtc</c> for Playback
    /// Reporting's stored strings and <c>TracearrService.TryParseUtc</c> for Tracearr's. This
    /// one takes an already-parsed <see cref="DateTime"/> whose <see cref="DateTime.Kind"/> was
    /// decided by someone else.
    /// </para>
    /// <para>
    /// On any stock server this is a no-op, and that is established rather than assumed.
    /// Jellyfin's SQLite provider installs a global value converter whose read direction is
    /// <c>DateTime.SpecifyKind(v, DateTimeKind.Utc)</c>, applied to every <c>DateTime</c> and
    /// <c>DateTime?</c> property, so <c>BaseItem.DateCreated</c> arrives as
    /// <see cref="DateTimeKind.Utc"/>. Verified at tag <c>v10.11.6</c>:
    /// <c>SqliteDatabaseProvider.cs:113-115</c> calls <c>SetDefaultDateTimeKind(Utc)</c>,
    /// <c>ModelBuilderExtensions.cs:42-45</c> attaches the converter to both types, and
    /// <c>ValueConverters/DateTimeKindValueConverter.cs:17</c> is the converter. The stored
    /// instant is genuinely UTC too — every set-site writes <c>CreationTimeUtc</c> or
    /// <c>DateTime.UtcNow</c>.
    /// </para>
    /// <para>
    /// So why exist at all? Because that guarantee belongs to the *SQLite provider*, not to
    /// Jellyfin's DbContext, and 10.11 admits plugin-supplied database providers. One that
    /// never calls <c>SetDefaultDateTimeKind</c> would hand back
    /// <see cref="DateTimeKind.Unspecified"/>, and finding 30 would come back — silently, in
    /// the one direction nothing here can observe. This makes the boundary explicit and costs
    /// a branch.
    /// </para>
    /// <para>
    /// It also matters that <see cref="DateTime"/> comparison ignores <see cref="DateTime.Kind"/>
    /// and compares raw ticks. An <see cref="DateTimeKind.Unspecified"/> or
    /// <see cref="DateTimeKind.Local"/> value tested against a UTC bound is therefore wrong
    /// without complaint, which is why this is applied before the grace cutoff and not only on
    /// the way to the wire.
    /// </para>
    /// </remarks>
    public static class JellyfinTimestamps
    {
        /// <summary>
        /// The same instant, carrying <see cref="DateTimeKind.Utc"/>.
        /// </summary>
        /// <param name="value">A timestamp read from a Jellyfin entity.</param>
        /// <returns>The UTC equivalent.</returns>
        public static DateTime AsUtc(DateTime value) => value.Kind switch
        {
            DateTimeKind.Utc => value,

            // A real offset to remove, so this is a conversion and not a relabelling.
            DateTimeKind.Local => value.ToUniversalTime(),

            // The residual assumption, and the only one: that a provider which did not label
            // the value still stored the UTC instant Jellyfin writes. Relabelling is right for
            // that case and ToUniversalTime would corrupt it by the server's offset.
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
        };

        /// <summary>
        /// The same instant as <see cref="AsUtc(DateTime)"/>, passing null through.
        /// </summary>
        /// <param name="value">A timestamp read from a Jellyfin entity, or null.</param>
        /// <returns>The UTC equivalent, or null.</returns>
        public static DateTime? AsUtc(DateTime? value)
            => value.HasValue ? AsUtc(value.Value) : null;
    }
}
