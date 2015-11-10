﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Discord.Commands
{
	/// <summary> A Discord.Net client with extensions for handling common bot operations like text commands. </summary>
	public partial class CommandService : IService
    {
		private readonly CommandServiceConfig _config;
		private readonly CommandGroupBuilder _root;
		private DiscordClient _client;

		public DiscordClient Client => _client;
		public CommandGroupBuilder Root => _root;

		//AllCommands store a flattened collection of all commands
		public IEnumerable<Command> AllCommands => _allCommands;
		private readonly List<Command> _allCommands;

		//Command map stores all commands by their input text, used for fast resolving and parsing
		private readonly CommandMap _map;

		//Groups store all commands by their module, used for more informative help
		internal IEnumerable<CommandMap> Categories => _categories.Values;
        private readonly Dictionary<string, CommandMap> _categories;

		public CommandService(CommandServiceConfig config)
		{
			_config = config;
			_allCommands = new List<Command>();
			_map = new CommandMap(null, "", "");
			_categories = new Dictionary<string, CommandMap>();
			_root = new CommandGroupBuilder(this, "", null);
		}

		void IService.Install(DiscordClient client)
		{
			_client = client;
			_config.Lock();

			if (_config.HelpMode != HelpMode.Disable)
            {
				CreateCommand("help")
					.Parameter("command", ParameterType.Multiple)
                    .Hide()
                    .Description("Returns information about commands.")
                    .Do((Func<CommandEventArgs, Task>)(async e =>
                    {
						Channel replyChannel = _config.HelpMode == HelpMode.Public ? e.Channel : await client.CreatePMChannel(e.User);
						if (e.Args.Length > 0) //Show command help
						{
							var map = _map.GetItem(string.Join(" ", e.Args));
							if (map != null)
								await ShowCommandHelp(map, e.User, e.Channel, replyChannel);
							else
								await client.SendMessage(replyChannel, "Unable to display help: Unknown command.");
						}
                        else //Show general help

/* Unmerged change from project 'Discord.Net.Commands'
Before:
							await ShowHelp(e.User, e.Channel, replyChannel);
After:
							await this.ShowHelp((User)e.User, e.Channel, replyChannel);
*/
							await this.ShowGeneralHelp(e.User, (Channel)e.Channel, (Channel)replyChannel);
                    }));
            }

            client.MessageReceived += async (s, e) =>
            {
                if (_allCommands.Count == 0)  return;
                if (e.Message.IsAuthor) return;

                string msg = e.Message.Text;
                if (msg.Length == 0) return;

				//Check for command char if one is provided
				var chars = _config.CommandChars;
                if (chars.Length > 0)
                {
					if (!chars.Contains(msg[0]))
						return;
                    msg = msg.Substring(1);
                }

				//Parse command
				Command command;
				int argPos;
				CommandParser.ParseCommand(msg, _map, out command, out argPos);				
				if (command == null)
				{
					CommandEventArgs errorArgs = new CommandEventArgs(e.Message, null, null);
					RaiseCommandError(CommandErrorType.UnknownCommand, errorArgs);
					return;
				}
				else
				{
					//Parse arguments
					string[] args;
					var error = CommandParser.ParseArgs(msg, argPos, command, out args);
                    if (error != null)
					{
						var errorArgs = new CommandEventArgs(e.Message, command, null);
						RaiseCommandError(error.Value, errorArgs);
						return;
					}
					
					var eventArgs = new CommandEventArgs(e.Message, command, args);

					// Check permissions
					if (!command.CanRun(eventArgs.User, eventArgs.Channel))
					{
						RaiseCommandError(CommandErrorType.BadPermissions, eventArgs);
						return;
					}

					// Run the command
					try
					{
						RaiseRanCommand(eventArgs);
						await command.Run(eventArgs).ConfigureAwait(false);
					}
					catch (Exception ex)
					{
						RaiseCommandError(CommandErrorType.Exception, eventArgs, ex);
					}
				}
            };
        }

        public Task ShowGeneralHelp(User user, Channel channel, Channel replyChannel = null)
        {
            StringBuilder output = new StringBuilder();
			/*output.AppendLine("These are the commands you can use:");
			output.Append(string.Join(", ", _map.SubCommands
                .Where(x => x.CanRun(user, channel) && !x.IsHidden)
                .Select(x => '`' + x.Text + '`' +
                (x.Aliases.Count() > 0 ? ", `" + string.Join("`, `", x.Aliases) + '`' : ""))));
			output.AppendLine("\nThese are the groups you can access:");
			output.Append(string.Join(", ", _map.SubGroups
				.Where(x => /*x.CanRun(user, channel)*//* && !x.IsHidden)
				.Select(x => '`' + x.Text + '`')));*/

			bool isFirstCategory = true;
			foreach (var category in _categories)
			{
				bool isFirstItem = true;
				foreach (var group in category.Value.SubGroups)
				{
					if (!group.IsHidden && group.CanRun(user, channel))
					{
						if (isFirstItem)
						{
							isFirstItem = false;
							//This is called for the first item in each category. If we never get here, we dont bother writing the header for a category type (since it's empty)
							if (isFirstCategory)
							{
								isFirstCategory = false;
								//Called for the first non-empty category
								output.AppendLine("These are the commands you can use:");
							}
							else
								output.AppendLine();
							if (category.Key != "")
							{
								output.Append(Format.Bold(category.Key));
								output.Append(": ");
							}
						}
						else
							output.Append(", ");
						output.Append('`');
						output.Append(group.Name);
						if (group.SubGroups.Any())
							output.Append("*");
						output.Append('`');
                    }
				}
			}

			if (output.Length == 0)
				output.Append("There are no commands you have permission to run.");
			else
			{
				output.Append("\n\n");

				var chars = _config.CommandChars;
				if (chars.Length > 0)
				{
					if (chars.Length == 1)
						output.AppendLine($"You can use `{chars[0]}` to call a command.");
					else
						output.AppendLine($"You can use `{string.Join(" ", chars.Take(chars.Length - 1))}` or `{chars.Last()}` to call a command.");
					output.AppendLine($"`{chars[0]}help <command>` can tell you more about how to use a command.");
				}
				else
					output.AppendLine($"`help <command>` can tell you more about how to use a command.");
			}

            return _client.SendMessage(replyChannel ?? channel, output.ToString());
        }

		private Task ShowCommandHelp(CommandMap map, User user, Channel channel, Channel replyChannel = null)
        {
			StringBuilder output = new StringBuilder();

			Command cmd = map.Command;
			if (cmd != null)
				ShowCommandHelpInternal(cmd, user, channel, output);
			else
			{
				output.Append('`');
				output.Append(map.FullName);
				output.Append("`\n");
			}

			bool isFirst = true;
			foreach (var subCmd in map.SubGroups.Where(x => x.CanRun(user, channel) && !x.IsHidden))
			{
				if (isFirst)
				{
					isFirst = false;
					output.AppendLine("Sub Commands: ");
				}
				else
					output.Append(", ");
				output.Append('`');
				output.Append(subCmd.Name);
				if (subCmd.SubGroups.Any())
					output.Append("*");
				output.Append('`');
			}

			if (isFirst)
				output.Append("There are no subcommands you have permission to run.");

			return _client.SendMessage(replyChannel ?? channel, output.ToString());
		}
		public Task ShowCommandHelp(Command command, User user, Channel channel, Channel replyChannel = null)
		{
			StringBuilder output = new StringBuilder();
			ShowCommandHelpInternal(command, user, channel, output);
            return _client.SendMessage(replyChannel ?? channel, output.ToString());
		}
		private void ShowCommandHelpInternal(Command command, User user, Channel channel, StringBuilder output)
		{
			output.Append('`');
			output.Append(command.Text);
			foreach (var param in command.Parameters)
			{
				switch (param.Type)
				{
					case ParameterType.Required:
						output.Append($" <{param.Name}>");
						break;
					case ParameterType.Optional:
						output.Append($" [{param.Name}]");
						break;
					case ParameterType.Multiple:
						output.Append(" [...]");
						break;
					case ParameterType.Unparsed:
						output.Append(" [--]");
						break;
				}
			}
			output.Append('`');
			output.AppendLine($": {command.Description ?? "No description set for this command."}");

			if (command.Aliases.Any())
				output.AppendLine($"Aliases: `" + string.Join("`, `", command.Aliases) + '`');
        }

		public void CreateGroup(string cmd, Action<CommandGroupBuilder> config = null) => _root.CreateGroup(cmd, config);
		public CommandBuilder CreateCommand(string cmd) => _root.CreateCommand(cmd);

		internal void AddCommand(Command command)
		{
			_allCommands.Add(command);

			//Get category
			CommandMap category;
            string categoryName = command.Category ?? "";
			if (!_categories.TryGetValue(categoryName, out category))
			{
				category = new CommandMap(null, "", "");
				_categories.Add(categoryName, category);
			}

			//Add main command
			category.AddCommand(command.Text, command);
            _map.AddCommand(command.Text, command);

			//Add aliases
			foreach (var alias in command.Aliases)
			{
				category.AddCommand(alias, command);
				_map.AddCommand(alias, command);
			}
		}
	}
}