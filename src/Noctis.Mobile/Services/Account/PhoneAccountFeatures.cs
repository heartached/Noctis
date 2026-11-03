namespace Noctis.Mobile.Services.Account;

/// <summary>
/// The phone's side of the desktop's parked account features (Noctis.Services.AccountFeatures):
/// signing in to the computer, streaming and downloading its songs, and sending plays and
/// favourites to it. While false the Android head builds no account service, so Settings shows
/// no Account or Downloads section, Listening History no Share with Computer, Scrobbling no
/// Through Computer route, and an account link left by an earlier build activates nothing (it
/// stays on disk for the build that ships it). Flip with the desktop's flag.
/// </summary>
public static class PhoneAccountFeatures
{
    public static readonly bool Enabled = false;
}
