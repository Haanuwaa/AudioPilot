using System.Text;

namespace AudioPilot.Cli
{
    internal static class CliShellCompletionGenerator
    {
        private sealed class CompletionNode
        {
            public SortedDictionary<string, CompletionNode> Children { get; } = new(StringComparer.Ordinal);
            public SortedSet<string> Flags { get; } = new(StringComparer.Ordinal);
            public SortedDictionary<string, FlagValueInfo> FlagValues { get; } = new(StringComparer.Ordinal);

            public CompletionNode GetOrAddChild(string literal)
            {
                if (!Children.TryGetValue(literal, out CompletionNode? child))
                {
                    child = new CompletionNode();
                    Children.Add(literal, child);
                }

                return child;
            }
        }

        private sealed record FlagValueInfo(bool ExpectsValue, string[] LiteralValues);
        private sealed record CompletionEntry(string[] Children, string[] Flags, string[] FlagsExpectingValues);
        private sealed record CompletionModel(
            IReadOnlyDictionary<string, CompletionEntry> Entries,
            IReadOnlyDictionary<string, string[]> FlagLiteralValues);

        internal static IReadOnlyList<string> DocumentedShellNames => ["powershell", "bash"];

        internal static bool TryNormalizeShell(string? shellName, out string? normalizedShell)
        {
            normalizedShell = null;
            if (string.IsNullOrWhiteSpace(shellName))
            {
                return false;
            }

            normalizedShell = shellName.Trim().ToLowerInvariant() switch
            {
                "powershell" => "powershell",
                "pwsh" => "powershell",
                "bash" => "bash",
                _ => null,
            };

            return normalizedShell != null;
        }

        internal static string GetScript(string shellName)
        {
            if (!TryNormalizeShell(shellName, out string? normalizedShell))
            {
                throw new ArgumentOutOfRangeException(nameof(shellName), shellName, "Unsupported completion shell.");
            }

            CompletionModel model = BuildModel();
            return normalizedShell switch
            {
                "powershell" => RenderPowerShellScript(model),
                "bash" => RenderBashScript(model),
                _ => throw new ArgumentOutOfRangeException(nameof(shellName), shellName, "Unsupported completion shell."),
            };
        }

        private static CompletionModel BuildModel()
        {
            var root = new CompletionNode();

            foreach (string usageLine in CliCommandHelpMetadata.UsageLines)
            {
                AddUsageLine(root, usageLine);
            }

            AddUsageLine(root, $"audio-pilot help [{CliCommandHelpMetadata.HelpTopicListForUsage}]");
            AddUsageLine(root, "audio-pilot version");

            foreach (string topic in CliCommandHelpMetadata.Topics)
            {
                root.GetOrAddChild(topic).GetOrAddChild("help");
            }

            var entries = new SortedDictionary<string, CompletionEntry>(StringComparer.Ordinal);
            var flagLiteralValues = new SortedDictionary<string, string[]>(StringComparer.Ordinal);
            FlattenModel(root, [], entries, flagLiteralValues);
            return new CompletionModel(entries, flagLiteralValues);
        }

        private static void AddUsageLine(CompletionNode root, string usageLine)
        {
            foreach (string variant in SplitTopLevelVariants(usageLine))
            {
                List<string> items = TokenizeUsageVariant(variant);
                if (items.Count == 0)
                {
                    continue;
                }

                int startIndex = string.Equals(items[0], "audio-pilot", StringComparison.Ordinal) ? 1 : 0;
                CollectTerminalNodes([root], items, startIndex, []);
            }
        }

        private static void CollectTerminalNodes(
            IReadOnlyCollection<CompletionNode> currentNodes,
            IReadOnlyList<string> items,
            int index,
            HashSet<CompletionNode> terminalNodes)
        {
            if (index >= items.Count)
            {
                foreach (CompletionNode node in currentNodes)
                {
                    terminalNodes.Add(node);
                }

                return;
            }

            string item = items[index];
            if (IsPlaceholderToken(item))
            {
                CollectTerminalNodes(currentNodes, items, index + 1, terminalNodes);
                return;
            }

            if (IsOptionalGroup(item))
            {
                string inner = item[1..^1];
                if (TryApplyOptionalFlagGroup(currentNodes, inner))
                {
                    CollectTerminalNodes(currentNodes, items, index + 1, terminalNodes);
                    return;
                }

                CollectTerminalNodes(currentNodes, items, index + 1, terminalNodes);

                var includedNodes = new HashSet<CompletionNode>();
                CollectTerminalNodes(currentNodes, TokenizeUsageVariant(inner), 0, includedNodes);
                CollectTerminalNodes(includedNodes, items, index + 1, terminalNodes);
                return;
            }

            HashSet<CompletionNode> nextNodes = [];
            foreach (string literal in SplitAlternatives(item))
            {
                if (IsPlaceholderToken(literal))
                {
                    foreach (CompletionNode node in currentNodes)
                    {
                        nextNodes.Add(node);
                    }

                    continue;
                }

                string normalizedLiteral = StripUsageAnnotations(literal);
                if (string.IsNullOrWhiteSpace(normalizedLiteral))
                {
                    continue;
                }

                foreach (CompletionNode node in currentNodes)
                {
                    nextNodes.Add(node.GetOrAddChild(normalizedLiteral));
                }
            }

            CollectTerminalNodes(nextNodes, items, index + 1, terminalNodes);
        }

        private static bool TryApplyOptionalFlagGroup(IReadOnlyCollection<CompletionNode> currentNodes, string inner)
        {
            if (string.IsNullOrWhiteSpace(inner) || !inner.StartsWith("--", StringComparison.Ordinal))
            {
                return false;
            }

            string[] alternatives = SplitAlternatives(inner);
            if (alternatives.Length == 0)
            {
                return false;
            }

            bool allAlternativesAreFlags = alternatives.All(static alternative => alternative.StartsWith("--", StringComparison.Ordinal));
            if (allAlternativesAreFlags)
            {
                foreach (string alternative in alternatives)
                {
                    string[] parts = [.. TokenizeUsageVariant(alternative)];
                    if (parts.Length == 0)
                    {
                        continue;
                    }

                    string flag = StripUsageAnnotations(parts[0]);
                    string[] literalValues = parts.Length > 1 && !IsPlaceholderToken(parts[1])
                        ? [.. SplitAlternatives(parts[1]).Select(StripUsageAnnotations).Where(static value => !string.IsNullOrWhiteSpace(value))]
                        : [];
                    bool expectsValue = parts.Length > 1;

                    foreach (CompletionNode node in currentNodes)
                    {
                        node.Flags.Add(flag);
                        if (expectsValue)
                        {
                            node.FlagValues[flag] = new FlagValueInfo(true, literalValues);
                        }
                    }
                }

                return true;
            }

            string[] groupParts = [.. TokenizeUsageVariant(inner)];
            if (groupParts.Length == 0 || !groupParts[0].StartsWith("--", StringComparison.Ordinal))
            {
                return false;
            }

            string groupFlag = StripUsageAnnotations(groupParts[0]);
            string[] groupLiteralValues = groupParts.Length > 1 && !IsPlaceholderToken(groupParts[1])
                ? [.. SplitAlternatives(groupParts[1]).Select(StripUsageAnnotations).Where(static value => !string.IsNullOrWhiteSpace(value))]
                : [];

            foreach (CompletionNode node in currentNodes)
            {
                node.Flags.Add(groupFlag);
                node.FlagValues[groupFlag] = new FlagValueInfo(groupParts.Length > 1, groupLiteralValues);
            }

            return true;
        }

        private static void FlattenModel(
            CompletionNode node,
            IReadOnlyList<string> pathParts,
            SortedDictionary<string, CompletionEntry> entries,
            SortedDictionary<string, string[]> flagLiteralValues)
        {
            string path = string.Join(' ', pathParts);
            string[] children = [.. node.Children.Keys];
            string[] flags = [.. node.Flags];
            string[] flagsExpectingValues = [.. node.FlagValues.Where(static pair => pair.Value.ExpectsValue).Select(static pair => pair.Key)];
            entries[path] = new CompletionEntry(children, flags, flagsExpectingValues);

            foreach ((string flag, FlagValueInfo info) in node.FlagValues)
            {
                flagLiteralValues[$"{path}|{flag}"] = info.LiteralValues;
            }

            foreach ((string childLiteral, CompletionNode childNode) in node.Children)
            {
                List<string> childPath = [.. pathParts, childLiteral];
                FlattenModel(childNode, childPath, entries, flagLiteralValues);
            }
        }

        private static List<string> SplitTopLevelVariants(string usageLine)
        {
            List<string> variants = [];
            var builder = new StringBuilder();
            int bracketDepth = 0;
            int angleDepth = 0;

            for (int index = 0; index < usageLine.Length; index++)
            {
                char ch = usageLine[index];
                switch (ch)
                {
                    case '[':
                        bracketDepth++;
                        break;
                    case ']':
                        bracketDepth--;
                        break;
                    case '<':
                        angleDepth++;
                        break;
                    case '>':
                        angleDepth--;
                        break;
                }

                bool isVariantSeparator = ch == '|'
                    && bracketDepth == 0
                    && angleDepth == 0
                    && index > 0
                    && index + 1 < usageLine.Length
                    && usageLine[index - 1] == ' '
                    && usageLine[index + 1] == ' ';

                if (isVariantSeparator)
                {
                    variants.Add(builder.ToString().Trim());
                    builder.Clear();
                    index++;
                    continue;
                }

                builder.Append(ch);
            }

            if (builder.Length > 0)
            {
                variants.Add(builder.ToString().Trim());
            }

            return variants;
        }

        private static List<string> TokenizeUsageVariant(string variant)
        {
            List<string> items = [];
            var builder = new StringBuilder();
            int bracketDepth = 0;
            int angleDepth = 0;

            foreach (char ch in variant)
            {
                if (ch == ' ' && bracketDepth == 0 && angleDepth == 0)
                {
                    if (builder.Length > 0)
                    {
                        items.Add(builder.ToString());
                        builder.Clear();
                    }

                    continue;
                }

                builder.Append(ch);
                switch (ch)
                {
                    case '[':
                        bracketDepth++;
                        break;
                    case ']':
                        bracketDepth--;
                        break;
                    case '<':
                        angleDepth++;
                        break;
                    case '>':
                        angleDepth--;
                        break;
                }
            }

            if (builder.Length > 0)
            {
                items.Add(builder.ToString());
            }

            return items;
        }

        private static string[] SplitAlternatives(string token)
        {
            List<string> alternatives = [];
            var builder = new StringBuilder();
            int bracketDepth = 0;
            int angleDepth = 0;

            foreach (char ch in token)
            {
                if (ch == '|' && bracketDepth == 0 && angleDepth == 0)
                {
                    alternatives.Add(builder.ToString().Trim());
                    builder.Clear();
                    continue;
                }

                builder.Append(ch);
                switch (ch)
                {
                    case '[':
                        bracketDepth++;
                        break;
                    case ']':
                        bracketDepth--;
                        break;
                    case '<':
                        angleDepth++;
                        break;
                    case '>':
                        angleDepth--;
                        break;
                }
            }

            if (builder.Length > 0)
            {
                alternatives.Add(builder.ToString().Trim());
            }

            return [.. alternatives];
        }

        private static bool IsOptionalGroup(string token)
        {
            return token.Length >= 2 && token[0] == '[' && token[^1] == ']';
        }

        private static bool IsPlaceholderToken(string token)
        {
            return token.Length >= 2 && token[0] == '<' && token[^1] == '>';
        }

        private static string StripUsageAnnotations(string token)
        {
            return token.Replace("(default)", string.Empty, StringComparison.Ordinal).Trim();
        }

        private static string RenderPowerShellScript(CompletionModel model)
        {
            var builder = new StringBuilder();
            builder.AppendLine("$AudioPilotCliCompletionChildren = @{");
            AppendPowerShellMap(builder, model.Entries, static entry => entry.Children);
            builder.AppendLine("}");
            builder.AppendLine();
            builder.AppendLine("$AudioPilotCliCompletionFlags = @{");
            AppendPowerShellMap(builder, model.Entries, static entry => entry.Flags);
            builder.AppendLine("}");
            builder.AppendLine();
            builder.AppendLine("$AudioPilotCliCompletionFlagsWithValues = @{");
            AppendPowerShellMap(builder, model.Entries, static entry => entry.FlagsExpectingValues);
            builder.AppendLine("}");
            builder.AppendLine();
            builder.AppendLine("$AudioPilotCliCompletionFlagValues = @{");
            foreach ((string key, string[] values) in model.FlagLiteralValues)
            {
                builder.Append("    '").Append(EscapePowerShellString(key)).Append("' = @(");
                builder.Append(string.Join(", ", values.Select(value => $"'{EscapePowerShellString(value)}'")));
                builder.AppendLine(")");
            }
            builder.AppendLine("}");
            builder.AppendLine();
            builder.AppendLine("function Get-AudioPilotCliCompletionItems { param([string]$Path, [switch]$Flags, [switch]$FlagsWithValues)");
            builder.AppendLine("    if ($Flags) { return @($AudioPilotCliCompletionFlags[$Path]) }");
            builder.AppendLine("    if ($FlagsWithValues) { return @($AudioPilotCliCompletionFlagsWithValues[$Path]) }");
            builder.AppendLine("    return @($AudioPilotCliCompletionChildren[$Path])");
            builder.AppendLine("}");
            builder.AppendLine();
            builder.AppendLine("function Resolve-AudioPilotCliCompletionState { param([string[]]$Tokens)");
            builder.AppendLine("    $pathParts = [System.Collections.Generic.List[string]]::new()");
            builder.AppendLine("    $index = 0");
            builder.AppendLine("    while ($index -lt $Tokens.Count) {");
            builder.AppendLine("        $path = [string]::Join(' ', $pathParts)");
            builder.AppendLine("        $token = $Tokens[$index]");
            builder.AppendLine("        $children = @(Get-AudioPilotCliCompletionItems -Path $path)");
            builder.AppendLine("        if ($children -contains $token) {");
            builder.AppendLine("            $pathParts.Add($token)");
            builder.AppendLine("            $index++");
            builder.AppendLine("            continue");
            builder.AppendLine("        }");
            builder.AppendLine("        $flags = @(Get-AudioPilotCliCompletionItems -Path $path -Flags)");
            builder.AppendLine("        if ($flags -contains $token) {");
            builder.AppendLine("            $flagsWithValues = @(Get-AudioPilotCliCompletionItems -Path $path -FlagsWithValues)");
            builder.AppendLine("            $expectsValue = $flagsWithValues -contains $token");
            builder.AppendLine("            $index++");
            builder.AppendLine("            if ($expectsValue) {");
            builder.AppendLine("                if ($index -ge $Tokens.Count) {");
            builder.AppendLine("                    return [pscustomobject]@{ Path = $path; AwaitingFlag = $token }");
            builder.AppendLine("                }");
            builder.AppendLine("                $index++");
            builder.AppendLine("            }");
            builder.AppendLine("            continue");
            builder.AppendLine("        }");
            builder.AppendLine("        $index++");
            builder.AppendLine("    }");
            builder.AppendLine("    return [pscustomobject]@{ Path = [string]::Join(' ', $pathParts); AwaitingFlag = $null }");
            builder.AppendLine("}");
            builder.AppendLine();
            builder.AppendLine("Register-ArgumentCompleter -Native -CommandName 'AudioPilot.Cli.exe', 'audio-pilot' -ScriptBlock {");
            builder.AppendLine("    param($wordToComplete, $commandAst, $cursorPosition)");
            builder.AppendLine("    $elements = @($commandAst.CommandElements | ForEach-Object { $_.Extent.Text })");
            builder.AppendLine("    if ($elements.Count -eq 0) { return }");
            builder.AppendLine("    $tokens = @($elements | Select-Object -Skip 1)");
            builder.AppendLine("    if ($tokens.Count -gt 0 -and $tokens[$tokens.Count - 1] -eq $wordToComplete) {");
            builder.AppendLine("        if ($tokens.Count -eq 1) { $tokens = @() } else { $tokens = @($tokens[0..($tokens.Count - 2)]) }");
            builder.AppendLine("    }");
            builder.AppendLine("    $state = Resolve-AudioPilotCliCompletionState -Tokens $tokens");
            builder.AppendLine("    if ($state.AwaitingFlag) {");
            builder.AppendLine("        $candidates = @($AudioPilotCliCompletionFlagValues[('{0}|{1}' -f $state.Path, $state.AwaitingFlag)])");
            builder.AppendLine("    } else {");
            builder.AppendLine("        $candidates = @(Get-AudioPilotCliCompletionItems -Path $state.Path) + @(Get-AudioPilotCliCompletionItems -Path $state.Path -Flags)");
            builder.AppendLine("    }");
            builder.AppendLine("    foreach ($candidate in $candidates | Sort-Object -Unique) {");
            builder.AppendLine("        if ($candidate -like ($wordToComplete + '*')) {");
            builder.AppendLine("            [System.Management.Automation.CompletionResult]::new($candidate, $candidate, 'ParameterValue', $candidate)");
            builder.AppendLine("        }");
            builder.AppendLine("    }");
            builder.AppendLine("}");
            return builder.ToString().TrimEnd();
        }

        private static void AppendPowerShellMap(
            StringBuilder builder,
            IReadOnlyDictionary<string, CompletionEntry> entries,
            Func<CompletionEntry, string[]> selector)
        {
            foreach ((string key, CompletionEntry entry) in entries)
            {
                string[] values = selector(entry);
                builder.Append("    '").Append(EscapePowerShellString(key)).Append("' = @(");
                builder.Append(string.Join(", ", values.Select(value => $"'{EscapePowerShellString(value)}'")));
                builder.AppendLine(")");
            }
        }

        private static string RenderBashScript(CompletionModel model)
        {
            var builder = new StringBuilder();
            builder.AppendLine("__audiopilot_children() {");
            builder.AppendLine("    case \"$1\" in");
            AppendBashCaseMap(builder, model.Entries, static entry => entry.Children);
            builder.AppendLine("        *) printf '%s' '' ;;");
            builder.AppendLine("    esac");
            builder.AppendLine("}");
            builder.AppendLine();
            builder.AppendLine("__audiopilot_flags() {");
            builder.AppendLine("    case \"$1\" in");
            AppendBashCaseMap(builder, model.Entries, static entry => entry.Flags);
            builder.AppendLine("        *) printf '%s' '' ;;");
            builder.AppendLine("    esac");
            builder.AppendLine("}");
            builder.AppendLine();
            builder.AppendLine("__audiopilot_flag_expects_value() {");
            builder.AppendLine("    case \"$1|$2\" in");
            foreach ((string path, CompletionEntry entry) in model.Entries)
            {
                foreach (string flag in entry.FlagsExpectingValues)
                {
                    builder.Append("        '").Append(EscapeBashString($"{path}|{flag}")).AppendLine("') return 0 ;;");
                }
            }
            builder.AppendLine("        *) return 1 ;;");
            builder.AppendLine("    esac");
            builder.AppendLine("}");
            builder.AppendLine();
            builder.AppendLine("__audiopilot_flag_values() {");
            builder.AppendLine("    case \"$1|$2\" in");
            foreach ((string key, string[] values) in model.FlagLiteralValues)
            {
                builder.Append("        '").Append(EscapeBashString(key)).Append("') printf '%s' '").Append(EscapeBashString(string.Join(' ', values))).AppendLine("' ;;");
            }
            builder.AppendLine("        *) printf '%s' '' ;;");
            builder.AppendLine("    esac");
            builder.AppendLine("}");
            builder.AppendLine();
            builder.AppendLine("__audiopilot_contains_word() {");
            builder.AppendLine("    local needle=$1");
            builder.AppendLine("    shift");
            builder.AppendLine("    local item");
            builder.AppendLine("    for item in \"$@\"; do");
            builder.AppendLine("        if [[ $item == $needle ]]; then");
            builder.AppendLine("            return 0");
            builder.AppendLine("        fi");
            builder.AppendLine("    done");
            builder.AppendLine("    return 1");
            builder.AppendLine("}");
            builder.AppendLine();
            builder.AppendLine("_audiopilot_complete() {");
            builder.AppendLine("    local cur=${COMP_WORDS[COMP_CWORD]}");
            builder.AppendLine("    local path=\"\"");
            builder.AppendLine("    local awaiting=\"\"");
            builder.AppendLine("    local index=1");
            builder.AppendLine("    while (( index < COMP_CWORD )); do");
            builder.AppendLine("        local token=${COMP_WORDS[index]}");
            builder.AppendLine("        local children_line=$(__audiopilot_children \"$path\")");
            builder.AppendLine("        read -r -a children <<< \"$children_line\"");
            builder.AppendLine("        if __audiopilot_contains_word \"$token\" \"${children[@]}\"; then");
            builder.AppendLine("            if [[ -n $path ]]; then");
            builder.AppendLine("                path+=\" \"");
            builder.AppendLine("            fi");
            builder.AppendLine("            path+=\"$token\"");
            builder.AppendLine("            ((index++))");
            builder.AppendLine("            continue");
            builder.AppendLine("        fi");
            builder.AppendLine("        local flags_line=$(__audiopilot_flags \"$path\")");
            builder.AppendLine("        read -r -a flags <<< \"$flags_line\"");
            builder.AppendLine("        if __audiopilot_contains_word \"$token\" \"${flags[@]}\"; then");
            builder.AppendLine("            ((index++))");
            builder.AppendLine("            if __audiopilot_flag_expects_value \"$path\" \"$token\"; then");
            builder.AppendLine("                if (( index >= COMP_CWORD )); then");
            builder.AppendLine("                    awaiting=$token");
            builder.AppendLine("                    break");
            builder.AppendLine("                fi");
            builder.AppendLine("                ((index++))");
            builder.AppendLine("            fi");
            builder.AppendLine("            continue");
            builder.AppendLine("        fi");
            builder.AppendLine("        ((index++))");
            builder.AppendLine("    done");
            builder.AppendLine("    local choices");
            builder.AppendLine("    if [[ -n $awaiting ]]; then");
            builder.AppendLine("        choices=$(__audiopilot_flag_values \"$path\" \"$awaiting\")");
            builder.AppendLine("    else");
            builder.AppendLine("        choices=\"$(__audiopilot_children \"$path\") $(__audiopilot_flags \"$path\")\"");
            builder.AppendLine("    fi");
            builder.AppendLine("    COMPREPLY=( $(compgen -W \"$choices\" -- \"$cur\") )");
            builder.AppendLine("    compopt -o default 2>/dev/null");
            builder.AppendLine("}");
            builder.AppendLine();
            builder.AppendLine("complete -F _audiopilot_complete AudioPilot.Cli.exe audio-pilot");
            return builder.ToString().TrimEnd();
        }

        private static void AppendBashCaseMap(
            StringBuilder builder,
            IReadOnlyDictionary<string, CompletionEntry> entries,
            Func<CompletionEntry, string[]> selector)
        {
            foreach ((string key, CompletionEntry entry) in entries)
            {
                builder.Append("        '").Append(EscapeBashString(key)).Append("') printf '%s' '").Append(EscapeBashString(string.Join(' ', selector(entry)))).AppendLine("' ;;");
            }
        }

        private static string EscapePowerShellString(string value)
        {
            return value.Replace("'", "''", StringComparison.Ordinal);
        }

        private static string EscapeBashString(string value)
        {
            return value.Replace("'", "'\\''", StringComparison.Ordinal);
        }
    }
}
