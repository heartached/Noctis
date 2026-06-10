namespace Noctis.Models;

/// <summary>Command parameter pairing a track with a 0-5 star rating for quick-rate menus.</summary>
public sealed record TrackRatingParameter(Track Track, int Rating);
