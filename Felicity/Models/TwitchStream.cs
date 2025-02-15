﻿using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

// ReSharper disable AutoPropertyCanBeMadeGetOnly.Global
// ReSharper disable UnusedMember.Global
// ReSharper disable PropertyCanBeMadeInitOnly.Global
// ReSharper disable UnusedAutoPropertyAccessor.Global

namespace Felicity.Models;

public class TwitchStream
{
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    [Key]
    public int Id { get; set; }

    public string TwitchName { get; set; } = string.Empty;
    public ulong ServerId { get; set; }
    public ulong ChannelId { get; set; }
    public ulong? UserId { get; set; }
    public ulong? MentionRole { get; set; }
    public bool MentionEveryone { get; set; }
}

public class ActiveStream
{
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    [Key]
    public int Id { get; set; }

    public int ConfigId { get; set; }
    public ulong StreamId { get; set; }
    public ulong MessageId { get; set; }
}

public class TwitchStreamDb : DbContext
{
    private readonly string _connectionString;

    public TwitchStreamDb(IConfiguration configuration)
    {
        _connectionString = configuration.GetConnectionString("MySQLDb");
    }

    public DbSet<TwitchStream> TwitchStreams { get; set; } = null!;
    public DbSet<ActiveStream> ActiveStreams { get; set; } = null!;

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        var serverVersion = new MariaDbServerVersion(new Version(10, 2, 21));
        optionsBuilder.UseMySql(_connectionString, serverVersion, builder => builder.EnableRetryOnFailure());
    }
}