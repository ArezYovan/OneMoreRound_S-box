using System;

/// <summary>
/// The best round you've ever walked out on, kept on disk between sessions. Only a night that ends by walking out
/// of the bar is ever offered here — going down scores nothing, so a knockout never touches it.
///
/// Written the moment it changes, to the game's own data folder, so a crash or alt-F4 afterwards can't lose it.
/// </summary>
public static class HighScore
{
	const string FileName = "highscore.json";

	/// <summary>What's on disk. Public, with a public property, so the JSON serializer can read and write it.</summary>
	public sealed class SaveData
	{
		public int Best { get; set; }
	}

	// read once and kept, so the menu can ask every frame without touching the disk
	static int? cached;

	/// <summary>The best round saved so far, or 0 if there isn't one yet.</summary>
	public static int Best => cached ??= Load();

	/// <summary>
	/// Offer a walked-out score. Saved if it beats the best so far; true when it did, so the end screen can say so.
	/// </summary>
	public static bool Submit( int score )
	{
		if ( score <= Best )
			return false;

		cached = score;

		try
		{
			FileSystem.Data.WriteJson( FileName, new SaveData { Best = score } );
		}
		catch ( Exception e )
		{
			// kept for the rest of this session even if the disk said no
			Log.Warning( $"[HighScore] couldn't save a best of {score}: {e.Message}" );
		}

		return true;
	}

	static int Load()
	{
		try
		{
			return Math.Max( 0, FileSystem.Data.ReadJsonOrDefault<SaveData>( FileName, null )?.Best ?? 0 );
		}
		catch ( Exception e )
		{
			// a damaged file reads as no score at all, rather than a menu that throws
			Log.Warning( $"[HighScore] couldn't read {FileName}: {e.Message}" );
			return 0;
		}
	}
}
