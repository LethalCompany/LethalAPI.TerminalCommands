﻿// -----------------------------------------------------------------------
// <copyright file="CommandHandler.cs" company="LethalAPI Modding Community">
// Copyright (c) LethalAPI Modding Community. All rights reserved.
// Licensed under the LGPL-3.0 license.
// </copyright>
// -----------------------------------------------------------------------

namespace LethalAPI.TerminalCommands.Models;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

using Interfaces;
using UnityEngine;

/// <summary>
/// Handles terminal command execution.
/// </summary>
public static class CommandHandler
{
    /// <summary>
    /// The interaction stack, for handling interaction layers.
    /// </summary>
    private static readonly Stack<ITerminalInteraction> Interactions = new ();

    /// <summary>
    /// Regex to split a command by spaces, while grouping sections of a command in quotations (").
    /// </summary>
    private static readonly Regex SplitRegex = new (@"[\""](.+?)[\""]|([^ ]+)", RegexOptions.Compiled | RegexOptions.ExplicitCapture | RegexOptions.IgnoreCase);

    /// <summary>
    /// Orders <see cref="TerminalCommand"/> instances by descending by priority, then by argument count.
    /// </summary>
    private static readonly CommandComparer Comparer = new ();

    /// <summary>
    /// Finds a list of matching command candidates, then tries to execute them in weighted order, returning the first response provided.
    /// </summary>
    /// <param name="command">Command text to parse and execute.</param>
    /// <param name="terminal">Terminal instance that raised the command.</param>
    /// <returns>A <see cref="TerminalNode"/> response, or <see langword="null"/> if execution should fall-through to the game's command handler.</returns>
    public static TerminalNode? TryExecute(string command, Terminal terminal)
    {
        MatchCollection matches = SplitRegex.Matches(command.Trim());
        List<string> commandParts = matches.Cast<Match>().Select(x => x.Value.Trim('"', ' ')).ToList();

        // Handle interactions if any
        if (Interactions.Count != 0 && Interactions.Pop() is { } interaction)
        {
            try
            {
                // Argument stream specifically for interactions, that provide the initial command name as part of arguments
                ArgumentStream interactionStream = new (commandParts.ToArray());

                // Fetch the service collection provided by the interaction, and add default services to it
                ServiceCollection interactServices = interaction.Services;
                interactServices.WithServices(interactionStream, terminal, interactionStream.Arguments);

                // Handle execution of the interaction
                object? interactionResult = interaction.HandleTerminalResponse(interactionStream);

                // Return result, or null cascade
                if (interactionResult != null)
                {
                    return HandleCommandResult(interactionResult);
                }
            }
            catch (Exception ex)
            {
                Log.Warn($"An error has occured while executing an interaction.");
                if(Plugin.Instance.Config.Debug)
                    Log.Exception(ex);
            }
        }

        // Handle command interpretation
        string commandName = commandParts.First();
        string[] commandArguments = commandParts.Skip(1).ToArray();

        List<(TerminalCommand Command, Func<TerminalNode> Invoker)> candidateCommands = new ();

        TerminalCommand[] overloads = TerminalRegistry.GetCommands(commandName).ToArray();

        ArgumentStream argumentStream = new (commandArguments);
        ServiceCollection services = new (commandArguments, argumentStream, terminal);

        // Evaluate candidates
        for (int i = 0; i < overloads.Length; i++)
        {
            TerminalCommand registeredCommand = overloads[i];

            if (!registeredCommand.CheckAllowed())
            {
                continue;
            }

            if (!registeredCommand.TryCreateInvoker(argumentStream, services, out Func<object>? invoker))
            {
                continue;
            }

            // A pass-though delegate to execute interactions, and return the response `TerminalNode` or null
            Func<TerminalNode> passThrough = () => HandleCommandResult(invoker());

            candidateCommands.Add((registeredCommand, passThrough));
        }

        // Execute candidates
        IOrderedEnumerable<(TerminalCommand Command, Func<TerminalNode> Invoker)> ordered = candidateCommands.OrderByDescending(x => x.Command, Comparer); // Order candidates descending by priority, then argument count

        foreach (var (_, invoker) in ordered)
        {
            TerminalNode? result = invoker();

            if (result != null)
            {
                return result;
            }
        }

        return null;
    }

    /// <summary>
    /// Pushes an interaction onto the terminal interaction stack, to execute next.
    /// </summary>
    /// <param name="interaction">Interaction to run.</param>
    public static void SetInteraction(ITerminalInteraction interaction)
    {
        Interactions.Push(interaction);
    }

    /// <summary>
    /// Handles the responses from commands, executing actions as needed.
    /// </summary>
    /// <param name="result">Result to parse into a <see cref="TerminalNode"/>.</param>
    /// <returns><see cref="TerminalNode"/> command display response.</returns>
    private static TerminalNode HandleCommandResult(object result)
    {
        if (result is TerminalNode node)
        {
            return node;
        }

        if (result is ITerminalInteraction interaction)
        {
            SetInteraction(interaction);
            return interaction.Prompt;
        }

        return ScriptableObject.CreateInstance<TerminalNode>()
            .WithDisplayText(result);
    }
}