/*
Copyright (c) FufuLauncher Dev Team. All rights reserved.
Licensed under the MIT License.
*/
using FufuLauncher.Constants;
using FufuLauncher.Helpers;

namespace FufuLauncher.Models.GameServer;

public sealed class GameServerScheme : IEquatable<GameServerScheme>
{
    private GameServerScheme(ChannelType channel, SubChannelType subChannel, bool isOversea,
        string launcherId, string gameId, string hypApi, string sophonApi, bool isNotCompatOnly)
    {
        Channel = channel;
        SubChannel = subChannel;
        IsOversea = isOversea;
        LauncherId = launcherId;
        GameId = gameId;
        HypApi = hypApi;
        SophonApi = sophonApi;
        IsNotCompatOnly = isNotCompatOnly;
    }
    public ChannelType Channel { get; }
    public SubChannelType SubChannel { get; }
    public bool IsOversea { get; }
    public string LauncherId { get; }
    public string GameId { get; }
    public string HypApi { get; }
    public string SophonApi { get; }
    public bool IsNotCompatOnly { get; }
    public string Cps => Channel == ChannelType.Bili ? "bilibili" : "mihoyo";
    public GameServerKind Kind => (Channel, IsOversea) switch
    {
        (ChannelType.Bili, false) => GameServerKind.ChineseBilibili,
        (_, false) => GameServerKind.ChineseOfficial,
        (_, true) => GameServerKind.Oversea,
    };
    
    public string DisplayName
    {
        get
        {
            string prefix = Kind switch
            {
                GameServerKind.ChineseBilibili => "GameServer_SchemePrefixBilibili".GetLocalized(),
                GameServerKind.ChineseOfficial => "GameServer_SchemePrefixOfficial".GetLocalized(),
                _ => "GameServer_SchemePrefixOversea".GetLocalized(),
            };

            return $"{prefix} | {Channel} | {SubChannel}";
        }
    }

    public bool Equals(GameServerScheme? other)
    {
        return other is not null
               && Channel == other.Channel
               && SubChannel == other.SubChannel
               && IsOversea == other.IsOversea;
    }

    public override bool Equals(object? obj) => obj is GameServerScheme other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(Channel, SubChannel, IsOversea);

    public override string ToString() => DisplayName;

    #region 实例

    private static GameServerScheme Chinese(ChannelType channel, SubChannelType subChannel, bool isNotCompatOnly = true)
    {
        return new GameServerScheme(channel, subChannel, false,
            GameConstants.CN_LAUNCHER_ID, GameConstants.CN_GAME_ID,
            ApiEndpoints.HypCnApi, ApiEndpoints.SophonCnApi, isNotCompatOnly);
    }

    private static GameServerScheme Oversea(ChannelType channel, SubChannelType subChannel, bool isNotCompatOnly = true)
    {
        return new GameServerScheme(channel, subChannel, true,
            GameConstants.OS_LAUNCHER_ID, GameConstants.OS_GAME_ID,
            ApiEndpoints.HypOsApi, ApiEndpoints.SophonOsApi, isNotCompatOnly);
    }

    private static GameServerScheme Bilibili(SubChannelType subChannel, bool isNotCompatOnly = true)
    {
        return new GameServerScheme(ChannelType.Bili, subChannel, false,
            GameConstants.BILI_LAUNCHER_ID, GameConstants.BILI_GAME_ID,
            ApiEndpoints.HypCnApi, ApiEndpoints.SophonCnApi, isNotCompatOnly);
    }

    #endregion

    #region 方案

    private static readonly GameServerScheme ServerChineseChannel00SubChannel00Compat = Chinese(ChannelType.Default, SubChannelType.Default, false);
    private static readonly GameServerScheme ServerChineseChannel00SubChannel01Compat = Chinese(ChannelType.Default, SubChannelType.Official, false);
    private static readonly GameServerScheme ServerChineseChannel01SubChannel00 = Chinese(ChannelType.Official, SubChannelType.Default);
    private static readonly GameServerScheme ServerChineseChannel01SubChannel01 = Chinese(ChannelType.Official, SubChannelType.Official);
    private static readonly GameServerScheme ServerChineseChannel01SubChannel02 = Chinese(ChannelType.Official, SubChannelType.NoTapTap);
    private static readonly GameServerScheme ServerChineseChannel01SubChannel03Compat = Chinese(ChannelType.Official, SubChannelType.Epic, false);
    private static readonly GameServerScheme ServerChineseChannel01SubChannel06Compat = Chinese(ChannelType.Official, SubChannelType.Google, false);
    private static readonly GameServerScheme ServerChineseChannel01SubChannel14Compat = Chinese(ChannelType.Official, (SubChannelType)14, false);
    private static readonly GameServerScheme ServerChineseChannel02SubChannel01Compat = Chinese(ChannelType.MiHoYoSONY, SubChannelType.Official, false);

    private static readonly GameServerScheme ServerChineseChannel14SubChannel00 = Bilibili(SubChannelType.Default);
    private static readonly GameServerScheme ServerChineseChannel14SubChannel01Compat = Bilibili(SubChannelType.Official, false);
    private static readonly GameServerScheme ServerChineseChannel14SubChannel02Compat = Bilibili(SubChannelType.NoTapTap, false);
    private static readonly GameServerScheme ServerChineseChannel14SubChannel06Compat = Bilibili(SubChannelType.Google, false);
    private static readonly GameServerScheme ServerChineseChannel14SubChannel14Compat = Bilibili((SubChannelType)14, false);
    private static readonly GameServerScheme ServerChineseChannel14SubChannel16Compat = Bilibili((SubChannelType)16, false);

    private static readonly GameServerScheme ServerOverseaChannel00SubChannel00Compat = Oversea(ChannelType.Default, SubChannelType.Default, false);
    private static readonly GameServerScheme ServerOverseaChannel01SubChannel00 = Oversea(ChannelType.Official, SubChannelType.Default);
    private static readonly GameServerScheme ServerOverseaChannel01SubChannel01 = Oversea(ChannelType.Official, SubChannelType.Official);
    private static readonly GameServerScheme ServerOverseaChannel01SubChannel02Compat = Oversea(ChannelType.Official, SubChannelType.NoTapTap, false);
    private static readonly GameServerScheme ServerOverseaChannel01SubChannel03 = Oversea(ChannelType.Official, SubChannelType.Epic);
    private static readonly GameServerScheme ServerOverseaChannel01SubChannel06 = Oversea(ChannelType.Official, SubChannelType.Google);
    private static readonly GameServerScheme ServerOverseaChannel02SubChannel01Compat = Oversea(ChannelType.MiHoYoSONY, SubChannelType.Official, false);
    private static readonly GameServerScheme ServerOverseaChannel14SubChannel00Compat = Oversea(ChannelType.Bili, SubChannelType.Default, false);
    private static readonly GameServerScheme ServerOverseaChannel14SubChannel14Compat = Oversea(ChannelType.Bili, (SubChannelType)14, false);
    
    public static IReadOnlyList<GameServerScheme> Known { get; } =
    [
        ServerChineseChannel00SubChannel00Compat,
        ServerChineseChannel00SubChannel01Compat,
        ServerChineseChannel01SubChannel00,
        ServerChineseChannel01SubChannel01,
        ServerChineseChannel01SubChannel02,
        ServerChineseChannel01SubChannel03Compat,
        ServerChineseChannel01SubChannel06Compat,
        ServerChineseChannel01SubChannel14Compat,
        ServerChineseChannel02SubChannel01Compat,

        ServerChineseChannel14SubChannel00,
        ServerChineseChannel14SubChannel01Compat,
        ServerChineseChannel14SubChannel02Compat,
        ServerChineseChannel14SubChannel06Compat,
        ServerChineseChannel14SubChannel14Compat,
        ServerChineseChannel14SubChannel16Compat,

        ServerOverseaChannel00SubChannel00Compat,
        ServerOverseaChannel01SubChannel00,
        ServerOverseaChannel01SubChannel01,
        ServerOverseaChannel01SubChannel02Compat,
        ServerOverseaChannel01SubChannel03,
        ServerOverseaChannel01SubChannel06,
        ServerOverseaChannel02SubChannel01Compat,
        ServerOverseaChannel14SubChannel00Compat,
        ServerOverseaChannel14SubChannel14Compat,
    ];
    
    public static IReadOnlyList<GameServerScheme> Selectable { get; } = Known.Where(scheme => scheme.IsNotCompatOnly).ToList();
    
    public static IReadOnlyList<GameServerScheme> BetaValues { get; } =
    [
        ServerChineseChannel01SubChannel01,
        ServerOverseaChannel01SubChannel00,
    ];

    public static GameServerScheme ChineseOfficialDefault => ServerChineseChannel01SubChannel00;
    public static GameServerScheme ChineseOfficialOfficial => ServerChineseChannel01SubChannel01;
    public static GameServerScheme ChineseOfficialNoTapTap => ServerChineseChannel01SubChannel02;
    public static GameServerScheme BilibiliDefault => ServerChineseChannel14SubChannel00;
    public static GameServerScheme OverseaOfficialDefault => ServerOverseaChannel01SubChannel00;
    public static GameServerScheme OverseaOfficialOfficial => ServerOverseaChannel01SubChannel01;
    public static GameServerScheme OverseaOfficialEpic => ServerOverseaChannel01SubChannel03;
    public static GameServerScheme OverseaOfficialGoogle => ServerOverseaChannel01SubChannel06;
    
    public static GameServerScheme FromPreset(string preset)
    {
        return preset switch
        {
            "Bili" => BilibiliDefault,
            "OS" => OverseaOfficialDefault,
            _ => ChineseOfficialOfficial,
        };
    }

    #endregion
}
