﻿using Discord;
using Discord.Interactions;
using DotNetBungieAPI.Models;
using DotNetBungieAPI.Models.Destiny;
using DotNetBungieAPI.Models.Destiny.Components;
using DotNetBungieAPI.Models.Destiny.Definitions.Collectibles;
using DotNetBungieAPI.Models.Destiny.Definitions.InventoryItems;
using DotNetBungieAPI.Models.Destiny.Responses;
using DotNetBungieAPI.Service.Abstractions;
using Felicity.Models;
using Felicity.Util;
using Felicity.Util.Enums;

// ReSharper disable ConditionIsAlwaysTrueOrFalseAccordingToNullableAPIContract

// ReSharper disable UnusedType.Global
// ReSharper disable UnusedMember.Global

namespace Felicity.DiscordCommands.Interactions;

[Group("lookup", "Various lookup commands for Destiny 2.")]
public class LookupCommands : InteractionModuleBase<ShardedInteractionContext>
{
    private readonly IBungieClient _bungieClient;
    private readonly ServerDb _serverDb;
    private readonly UserDb _userDb;

    public LookupCommands(UserDb userDb, IBungieClient bungieClient, ServerDb serverDb)
    {
        _userDb = userDb;
        _bungieClient = bungieClient;
        _serverDb = serverDb;
    }

    [SlashCommand("wish", "Look up patterns for wishes in the Last Wish raid.")]
    public async Task LookupWish(
        [Summary("wish", "Which wish do you need?")] [Autocomplete(typeof(WishAutocomplete))]
        int wishNumber)
    {
        await DeferAsync();

        var wish = Wishes.KnownWishes[wishNumber - 1];

        var embed = new EmbedBuilder
        {
            Color = Color.Purple,
            Description = wish.Description,
            Footer = Embeds.MakeFooter(),
            ImageUrl = $"https://cdn.tryfelicity.one/images/wishes/wish-{wishNumber}.png",
            ThumbnailUrl = "https://bungie.net/common/destiny2_content/icons/fc5791eb2406bf5e6b361f3d16596693.png",
            Title = $"Wish {wish.Number}: {wish.Name}"
        };

        await FollowupAsync(embed: embed.Build());
    }

    [SlashCommand("account-share", "Look up account shared emblems of a player.")]
    public async Task LookupAccountShare(
        [Summary("bungie-name",
            "Bungie name of the requested user (name#1234).")]
        string bungieTag = "")
    {
        await DeferAsync();

        if (!string.IsNullOrEmpty(bungieTag) && !bungieTag.Contains('#'))
        {
            var errorEmbed = Embeds.MakeErrorEmbed();
            errorEmbed.Description =
                $"`{bungieTag}` is not a correct format for a Bungie name.\nTry again with the `<name>#<number>` format.";
            await FollowupAsync(embed: errorEmbed.Build());
            return;
        }

        long membershipId;
        BungieMembershipType membershipType;
        string bungieName;

        if (string.IsNullOrEmpty(bungieTag))
        {
            var currentUser = _userDb.Users.FirstOrDefault(x => x.DiscordId == Context.User.Id);

            if (currentUser == null)
            {
                var errorEmbed = Embeds.MakeErrorEmbed();
                errorEmbed.Description =
                    "You did not specify a Bungie name to lookup, so this command defaults to your current user, however you are not registered.\n" +
                    "Please `/user register` and try again, or specify a name to lookup.";
                await FollowupAsync(embed: errorEmbed.Build());
                return;
            }

            membershipId = currentUser.DestinyMembershipId;
            membershipType = currentUser.DestinyMembershipType;
            bungieName = currentUser.BungieName;
        }
        else
        {
            var name = bungieTag.Split("#").First();
            var code = Convert.ToInt16(bungieTag.Split("#").Last());

            var goodProfile = await BungieApiUtils.GetLatestProfile(_bungieClient, name, code);
            if (goodProfile == null)
            {
                var errorEmbed = Embeds.MakeErrorEmbed();
                errorEmbed.Description = $"No profiles found matching `{bungieTag}`.";
                await FollowupAsync(embed: errorEmbed.Build());
                return;
            }

            membershipId = goodProfile.MembershipId;
            membershipType = goodProfile.MembershipType;
            bungieName = $"{goodProfile.BungieGlobalDisplayName}#{goodProfile.BungieGlobalDisplayNameCode}";
        }

        var profile = await _bungieClient.ApiAccess.Destiny2.GetProfile(membershipType, membershipId, new[]
        {
            DestinyComponentType.Characters, DestinyComponentType.Profiles, DestinyComponentType.Collectibles
        });

        if (profile.Response.ProfileCollectibles.Data == null)
        {
            var errorEmbed = Embeds.MakeErrorEmbed();
            errorEmbed.Description = $"`{bungieTag}` has their collections set to private, unable to parse emblems.";

            await FollowupAsync(embed: errorEmbed.Build());
            return;
        }

        var emblemCount = 0;
        var emblemList = new List<DestinyCollectibleDefinition>();

        var manifestInventoryItemIDs = profile.Response.Characters.Data
            .Select(destinyCharacterComponent => destinyCharacterComponent.Value.Emblem.Hash).ToList();
        var manifestCollectibleIDs =
            profile.Response.ProfileCollectibles.Data.Collectibles.Select(collectible => collectible.Key).ToList();

        var lg = MiscUtils.GetLanguage(Context.Guild, _serverDb);

        var manifestInventoryItems = new List<DestinyInventoryItemDefinition>();
        foreach (var destinyInventoryItemDefinition in manifestInventoryItemIDs)
        {
            _bungieClient.Repository.TryGetDestinyDefinition<DestinyInventoryItemDefinition>(
                (uint)destinyInventoryItemDefinition!, lg, out var result);

            manifestInventoryItems.Add(result);
        }

        var manifestCollectibles = new List<DestinyCollectibleDefinition>();
        foreach (var definitionHashPointer in manifestCollectibleIDs)
        {
            _bungieClient.Repository.TryGetDestinyDefinition<DestinyCollectibleDefinition>(
                (uint)definitionHashPointer.Hash!, lg, out var result);

            manifestCollectibles.Add(result);
        }

        foreach (var collectible in from collectible in manifestCollectibles
                                    where !collectible.Redacted
                                    where !string.IsNullOrEmpty(collectible.DisplayProperties.Name)
                                    from manifestCollectibleParentNodeHash in collectible.ParentNodes
                                    where EmblemCats.EmblemCatList.Contains((EmblemCat)manifestCollectibleParentNodeHash.Hash!)
                                    select collectible)
        {
            emblemCount++;

            var value = profile.Response.ProfileCollectibles.Data.Collectibles[collectible.Hash];

            foreach (var unused in from emblem in manifestInventoryItems
                                   where emblem.Collectible.Hash == collectible.Hash
                                   where value.State.HasFlag(DestinyCollectibleState.NotAcquired)
                                   where !emblemList.Contains(collectible)
                                   select emblem) emblemList.Add(collectible);

            if (value.State.HasFlag(DestinyCollectibleState.Invisible) &&
                !value.State.HasFlag(DestinyCollectibleState.NotAcquired))
                if (!emblemList.Contains(collectible))
                    emblemList.Add(collectible);

            // ReSharper disable once InvertIf
            if (value.State.HasFlag(DestinyCollectibleState.UniquenessViolation) &&
                value.State.HasFlag(DestinyCollectibleState.NotAcquired))
                if (!emblemList.Contains(collectible))
                    emblemList.Add(collectible);
        }

        var sortedList = emblemList.OrderBy(o => o.DisplayProperties.Name).ToList();

        var embed = new EmbedBuilder
        {
            Title = bungieName,
            Url =
                $"https://www.bungie.net/7/en/User/Profile/{(int)profile.Response.Profile.Data.UserInfo.MembershipType}/" +
                profile.Response.Profile.Data.UserInfo.MembershipId,
            Color = Color.Purple,
            ThumbnailUrl = BotVariables.BungieBaseUrl + profile.Response.Characters.Data.First().Value.EmblemPath,
            Footer = Embeds.MakeFooter()
        };

        if (sortedList.Count == 0)
        {
            embed.Description = "Account has no shared emblems.";
        }
        else
        {
            embed.Description = "**Account shared emblems:**\n";

            foreach (var emblemDefinition in sortedList)
                embed.Description +=
                    $"> [{emblemDefinition.DisplayProperties.Name}](https://destinyemblemcollector.com/emblem?id={emblemDefinition.Item.Hash})\n";
        }

        embed.AddField("Parsed", $"> {emblemCount}", true);
        embed.AddField("Shared", $"> {sortedList.Count}", true);

        await FollowupAsync(embed: embed.Build());
    }

    [SlashCommand("guardian", "Look up a profile of a player.")]
    public async Task LookupGuardian(
        [Summary("bungie-name", "Bungie name of the requested user (name#1234). Defaults to your own.")]
        string bungieTag = "")
    {
        await DeferAsync();

        long membershipId;
        BungieMembershipType membershipType;
        string bungieName;

        if (string.IsNullOrEmpty(bungieTag))
        {
            var linkedUser = _userDb.Users.FirstOrDefault(x => x.DiscordId == Context.User.Id);

            if (linkedUser == null)
            {
                await FollowupAsync("You aren't registered and didn't provide a bungie name.");
                return;
            }

            var linkedProfile = await BungieApiUtils.GetLatestProfile(_bungieClient, linkedUser.BungieMembershipId,
                BungieMembershipType.BungieNext);

            membershipId = linkedProfile.MembershipId;
            membershipType = linkedProfile.MembershipType;
            bungieName = $"{linkedProfile.BungieGlobalDisplayName}#{linkedProfile.BungieGlobalDisplayNameCode}";
        }
        else
        {
            var name = bungieTag.Split("#").First();
            var code = Convert.ToInt16(bungieTag.Split("#").Last());

            var goodProfile = await BungieApiUtils.GetLatestProfile(_bungieClient, name, code);
            if (goodProfile == null)
            {
                var errorEmbed = Embeds.MakeErrorEmbed();
                errorEmbed.Description = $"No profiles found matching `{bungieTag}`.";
                await FollowupAsync(embed: errorEmbed.Build());
                return;
            }

            membershipId = goodProfile.MembershipId;
            membershipType = goodProfile.MembershipType;
            bungieName = $"{goodProfile.BungieGlobalDisplayName}#{goodProfile.BungieGlobalDisplayNameCode}";
        }

        var player = _bungieClient.ApiAccess.Destiny2.GetProfile(membershipType, membershipId, new[]
        {
            DestinyComponentType.Characters
        });

        await FollowupAsync(embed: GenerateLookupEmbed(await player, bungieName, membershipId, membershipType));
    }

    private static Embed GenerateLookupEmbed(BungieResponse<DestinyProfileResponse> playerResponse, string bungieName,
        long membershipId, BungieMembershipType membershipType)
    {
        DestinyCharacterComponent? goodChar = null;

        var lastPlayed = new DateTime();
        foreach (var (_, value) in playerResponse.Response.Characters.Data.Where(destinyCharacterComponent =>
                     destinyCharacterComponent.Value.DateLastPlayed > lastPlayed))
        {
            lastPlayed = value.DateLastPlayed;
            goodChar = value;
        }

        if (goodChar == null)
        {
            var errorEmbed = Embeds.MakeErrorEmbed();
            errorEmbed.Description = "Failed to find players characters.";
            return errorEmbed.Build();
        }

        var embed = new EmbedBuilder
        {
            Color = Color.DarkMagenta,
            Title = bungieName,
            Footer = Embeds.MakeFooter(),
            Description =
                $"{Format.Code($"/invite {bungieName}")} | " +
                $"{Format.Code($"/join {bungieName}")}",
            ThumbnailUrl = BotVariables.BungieBaseUrl + goodChar.EmblemPath
        };

        var memTypeAndId = $"{(int)membershipType}/{membershipId}";

        embed.AddField("General",
            $"[Braytech](https://bray.tech/{memTypeAndId})\n" +
            $"[D2Timeline](https://mijago.github.io/D2Timeline/#/display/{memTypeAndId})\n" +
            $"[Guardian.Report](https://guardian.report/?view=PVE&guardians={membershipId})\n", true);
        embed.AddField("PvE",
            $"[Dungeons]({GetReportLink(membershipType, membershipId, "dungeon")})\n" +
            $"[Nightfalls](https://nightfall.report/guardian/{memTypeAndId})\n" +
            $"[Raids]({GetReportLink(membershipType, membershipId, "raid")})", true);
        embed.AddField("PvP",
            $"[Crucible](https://crucible.report/report/{memTypeAndId})\n" +
            $"[DestinyTracker](https://destinytracker.com/destiny-2/profile/bungie/{membershipId}/overview?perspective=pvp)\n" +
            $"[Trials](https://trials.report/report/{memTypeAndId})", true);

        return embed.Build();
    }

    private static string GetReportLink(BungieMembershipType membershipType, long membershipId, string reportType)
    {
        string platform;
        // ReSharper disable once SwitchStatementHandlesSomeKnownEnumValuesWithDefault
        switch (membershipType)
        {
            case BungieMembershipType.TigerXbox:
                platform = "xb";
                break;
            case BungieMembershipType.TigerPsn:
                platform = "ps";
                break;
            case BungieMembershipType.TigerSteam:
                platform = "pc";
                break;
            case BungieMembershipType.TigerStadia:
                platform = "stadia";
                break;
            default:
                return $"https://{reportType}.report";
        }

        return $"https://{reportType}.report/{platform}/{membershipId}";
    }
}