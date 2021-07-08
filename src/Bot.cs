﻿namespace iPhoneController
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading.Tasks;

    using DSharpPlus;
    using DSharpPlus.CommandsNext;
    using DSharpPlus.Entities;
    using DSharpPlus.EventArgs;

    using iPhoneController.Commands;
    using iPhoneController.Configuration;
    using iPhoneController.Diagnostics;
    using iPhoneController.Extensions;
    using iPhoneController.Net;
    using Microsoft.Extensions.DependencyInjection;

    public class Bot
    {
        private static readonly IEventLogger _logger = EventLogger.GetLogger("BOT");

        private readonly Dictionary<ulong, DiscordClient> _guilds;
        private readonly CommandsNextExtension _commands;
        private readonly Config _config;
        private readonly HttpServer _server;

        public Bot(Config config)
        {
            _logger.Trace($"Config [Host={config.Host}, Port={config.Port}, Servers={config.Servers.Count:N0}]");
            _config = config;
            _guilds = new Dictionary<ulong, DiscordClient>();

            AppDomain.CurrentDomain.UnhandledException += async (sender, e) =>
            {
                _logger.Debug("Unhandled exception caught.");
                _logger.Error((Exception)e.ExceptionObject);

                if (e.IsTerminating)
                {
                    foreach (var (_, guild) in _guilds)
                    {
                        if (guild != null)
                        {
                            foreach (var (serverId, server) in _config.Servers)
                            {
                                if (!guild.Guilds.ContainsKey(serverId))
                                    continue;

                                var owner = await guild.Guilds[serverId].GetMemberAsync(server.OwnerId);
                                if (owner == null)
                                {
                                    _logger.Warn($"Failed to get owner from id {server.OwnerId}.");
                                    return;
                                }
                                await owner.SendDirectMessage(Strings.CrashMessage, null);
                            }
                        }
                    }
                }
            };

            foreach (var (guildId, guildConfig) in _config.Servers)
            {
                var client = new DiscordClient(new DiscordConfiguration
                {
                    AutoReconnect = true,
                    AlwaysCacheMembers = true,
                    GatewayCompressionLevel = GatewayCompressionLevel.Payload,
                    Token = guildConfig.Token,
                    TokenType = TokenType.Bot,
                    MinimumLogLevel = Microsoft.Extensions.Logging.LogLevel.Error,
                    Intents = DiscordIntents.DirectMessages
                        | DiscordIntents.GuildMembers
                        | DiscordIntents.GuildMessages
                        | DiscordIntents.GuildMessageTyping
                        | DiscordIntents.GuildPresences
                        | DiscordIntents.Guilds,
                    ReconnectIndefinitely = true,
                });
                client.Ready += Client_Ready;
                client.ClientErrored += Client_ClientErrored;
                //client.DebugLogger.LogMessageReceived += DebugLogger_LogMessageReceived;

                var servicesCol = new ServiceCollection()
                    .AddSingleton(typeof(Config), _config);
                var services = servicesCol.BuildServiceProvider();

                _commands = client.UseCommandsNext
                (
                    new CommandsNextConfiguration
                    {
                        StringPrefixes = new[] { guildConfig.CommandPrefix?.ToString() },
                        EnableDms = true,
                        EnableMentionPrefix = string.IsNullOrEmpty(guildConfig.CommandPrefix),
                        EnableDefaultHelp = false,
                        CaseSensitive = false,
                        IgnoreExtraArguments = true,
                        Services = services,
                    }
                );
                _guilds.Add(guildId, client);
            }
            _commands.CommandExecuted += Commands_CommandExecuted;
            _commands.CommandErrored += Commands_CommandErrored;
            _commands.RegisterCommands<AppDeployment>();
            _commands.RegisterCommands<PhoneControl>();
            _server = new HttpServer(_config.Host, _config.Port);
        }

        public void Start()
        {
            _logger.Trace("Start");
            _logger.Info("Connecting to Discord...");

            foreach (var (_, guild) in _guilds)
            {
                guild.ConnectAsync();
                _server.Start();
            }
        }

        public void Stop()
        {
            _logger.Trace("Stop");
            _logger.Info("Disconnecting Discord client...");

            foreach (var (_, guild) in _guilds)
            {
                guild.DisconnectAsync();
            }
            _server.Stop();
        }

        #region Discord Events

        private async Task Client_Ready(DiscordClient client, ReadyEventArgs e)
        {
            _logger.Info($"[DISCORD] Connected.");
            _logger.Info($"[DISCORD] Current Application:");
            _logger.Info($"[DISCORD] Name: {client.CurrentApplication.Name}");
            _logger.Info($"[DISCORD] Description: {client.CurrentApplication.Description}");
            var owners = string.Join("\n", client.CurrentApplication.Owners.Select(x => $"{x.Username}#{x.Discriminator}"));
            _logger.Info($"[DISCORD] Owner: {owners}");
            _logger.Info($"[DISCORD] Current User:");
            _logger.Info($"[DISCORD] Id: {client.CurrentUser.Id}");
            _logger.Info($"[DISCORD] Name: {client.CurrentUser.Username}#{client.CurrentUser.Discriminator}");
            _logger.Info($"[DISCORD] Email: {client.CurrentUser.Email}");
            _logger.Info($"Machine Name: {Environment.MachineName}");

            await Task.CompletedTask;
        }

        private async Task Client_ClientErrored(DiscordClient client, ClientErrorEventArgs e)
        {
            _logger.Error(e.Exception);

            await Task.CompletedTask;
        }

        private async Task Commands_CommandExecuted(CommandsNextExtension commands, CommandExecutionEventArgs e)
        {
            // let's log the name of the command and user
            _logger.Debug($"{e.Context.User.Username} successfully executed '{e.Command.QualifiedName}'");

            // since this method is not async, let's return
            // a completed task, so that no additional work
            // is done
            await Task.CompletedTask;
        }

        private async Task Commands_CommandErrored(CommandsNextExtension commands, CommandErrorEventArgs e)
        {
            _logger.Error($"{e.Context.User.Username} tried executing '{e.Command?.QualifiedName ?? e.Context.Message.Content}' but it errored: {e.Exception.GetType()}: {e.Exception.Message ?? "<no message>"}", DateTime.Now);

            // let's check if the error is a result of lack of required permissions
            if (e.Exception is DSharpPlus.CommandsNext.Exceptions.ChecksFailedException)
            {
                // The user lacks required permissions, 
                var emoji = DiscordEmoji.FromName(e.Context.Client, ":no_entry:");

                // let's wrap the response into an embed
                var embed = new DiscordEmbedBuilder
                {
                    Title = "Access denied",
                    Description = $"{emoji} You do not have the permissions required to execute this command.",
                    Color = new DiscordColor(0xFF0000) // red
                };
                await e.Context.RespondAsync(string.Empty, embed: embed);
            }
            else if (e.Exception is ArgumentException)
            {
                var arguments = e.Command.Overloads.First();
                // The user lacks required permissions, 
                var emoji = DiscordEmoji.FromName(e.Context.Client, ":x:");

                var example = $"Command Example: ```<prefix>{e.Command.Name} {string.Join(" ", arguments.Arguments.Select(x => x.IsOptional ? $"[{x.Name}]" : x.Name))}```\r\n*Parameters in brackets are optional.*";

                // let's wrap the response into an embed
                var embed = new DiscordEmbedBuilder
                {
                    Title = $"{emoji} Invalid Argument(s)",
                    Description = $"{string.Join(Environment.NewLine, arguments.Arguments.Select(x => $"Parameter **{x.Name}** expects type **{x.Type}.**"))}.\r\n\r\n{example}",
                    Color = new DiscordColor(0xFF0000) // red
                };
                await e.Context.RespondAsync(string.Empty, embed: embed);
            }
            else if (e.Exception is DSharpPlus.CommandsNext.Exceptions.CommandNotFoundException)
            {
                _logger.Warn($"User {e.Context.User.Username} tried executing command {e.Context.Message.Content} but command does not exist.");
            }
            else
            {
                _logger.Error($"User {e.Context.User.Username} tried executing command {e.Command?.Name} and unknown error occurred.\r\n: {e.Exception}");
            }
        }

        /*
        private void DebugLogger_LogMessageReceived(object sender, DebugLogMessageEventArgs e)
        {
            if (e.Application == "REST")
            {
                _logger.Error("[DISCORD] RATE LIMITED-----------------");
                return;
            }

            //Color
            ConsoleColor color;
            switch (e.Level)
            {
                case DSharpPlus.LogLevel.Error: color = ConsoleColor.DarkRed; break;
                case DSharpPlus.LogLevel.Warning: color = ConsoleColor.Yellow; break;
                case DSharpPlus.LogLevel.Info: color = ConsoleColor.White; break;
                case DSharpPlus.LogLevel.Critical: color = ConsoleColor.Red; break;
                case DSharpPlus.LogLevel.Debug: default: color = ConsoleColor.DarkGray; break;
            }

            //Source
            var sourceName = e.Application;

            //Text
            var text = e.Message;

            //Build message
            var builder = new System.Text.StringBuilder(text.Length + (sourceName?.Length ?? 0) + 5);
            if (sourceName != null)
            {
                builder.Append('[');
                builder.Append(sourceName);
                builder.Append("] ");
            }

            for (var i = 0; i < text.Length; i++)
            {
                //Strip control chars
                var c = text[i];
                if (!char.IsControl(c))
                    builder.Append(c);
            }

            if (text != null)
            {
                builder.Append(": ");
                builder.Append(text);
            }

            text = builder.ToString();
            Console.ForegroundColor = color;
            Console.WriteLine(text);
            Console.ResetColor();
        }
        */

        #endregion
    }
}