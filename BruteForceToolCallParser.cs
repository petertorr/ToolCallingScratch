using Betalgo.Ranul.OpenAI.ObjectModels.RequestModels;
using Betalgo.Ranul.OpenAI.ObjectModels.SharedModels;
using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

namespace ToolCallingScratch
{
    internal static class BruteForceToolCallParser
    {

        // Letter or underscore, followed by any number of letters, underscores, digits, or periods
        // (Normal identifiers don't allow periods, but for tools people may like to use "noun.verb"
        // so it is allowed here
        const string identifierRegEx = """
            [a-zA-Z_](?:[a-zA-Z_0-9\.])*
            """;

        // A quote, followed by zero or more non-quote chars OR escaped quotes (with no embedded newlines).
        const string quotedStringRegEx = """
            "(?:[^"\r\n]|(?:\"))*?"
            """;

        // 1 or more digits followed by an optional period and one or more digits.
        // JSON doesn't support a leading decimal point (like ".1234") we don't match that.
        const string numberRegEx = """
            \d+(?:\.\d+)?
            """;

        const string boolRegEx = """
            true|false
            """;

        const string nullRegEx = """
            null
            """;

        static readonly Regex propertyNameRegex = new($"(?<identifier>{identifierRegEx})|(?<string>{quotedStringRegEx})", RegexOptions.Multiline);

        static readonly Regex propertyValueRegex = new($"(?<string>{quotedStringRegEx})|(?<number>{numberRegEx})|(?<boolean>{boolRegEx})|(?<null>{nullRegEx})", RegexOptions.IgnoreCase);

        /// <summary>
        /// Tries to brute-force a function call from message content. It's based on a pedestrian 
        /// parsing of the text for appropriate names / values in the right order. Explicitly does
        /// not require JSON parsing, as many models return partial JSON, XML, or other formats.
        /// </summary>
        /// <param name="rawMessageContent">The raw content of the message.</param>
        /// <param name="tools">The set of registered tools to check for.</param>
        /// <param name="function">If successful, the detected function call.</param>
        /// <returns>True if a function call was detected; otherwise false</returns>
        /// <remarks>
        /// For each ToolDefinition in 'tools', the parser does the following:
        /// 
        /// 1) Check for last occurence of its name (to skip over any prose like "I need to call...").
        ///    * If there is none, go to next tool.
        ///
        /// 2) Parse the rest of the string into the following tokens (all other chars are ignored):
        ///    * Identifier (typical C-style identifier, but also allows embedded periods).
        ///    * Quoted String (including the empty string and strings with escaped quotes).
        ///    * Boolean literal.
        ///    * Numeric literal (including floats).
        ///    * null literal.
        ///
        /// 3) Attempts to match the remaining tokens against the arguments defined by
        ///    the tool, in the form of <property-name> followed by <property-value>.
        ///    * Any string that isn't a valid argument name is ignored
        ///    * Only simple properties are supported (not arrays or nested objects).
        ///
        /// Arguments can be in any order, but this function assumes ALL defined parameters are required.
        /// </remarks>
        internal static bool TryBruteForceFunctionCall(string rawMessageContent, IEnumerable<ToolDefinition> tools, [NotNullWhen(true)] out FunctionCall? function)
        {
            function = null;
            foreach (var tool in tools)
            {
                var functionName = tool.Function!.Name;
                var parameters = tool.Function!.Parameters.Properties!;
                var arguments = new Dictionary<string, string>();

                var quotedFunctionName = $@"""{functionName}""";
                int foundLength;
                int index = rawMessageContent.LastIndexOf(quotedFunctionName);
                if (index >= 0)
                {
                    foundLength = quotedFunctionName.Length;
                }
                else
                {
                    // Look for unquoted name, like a C-style function call
                    index = rawMessageContent.LastIndexOf(functionName);
                    if (index < 0)
                    {
                        continue;
                    }
                    foundLength = functionName.Length;
                }

                int offset = index + foundLength;
                while (offset < rawMessageContent.Length && arguments.Count < parameters.Count)
                {
                    // Search for the next property name
                    var match = propertyNameRegex.Match(rawMessageContent, offset);
                    offset = match.Index + match.Length;
                    if (!match.Success)
                    {
                        // No tokens; can't be a call.
                        break;
                    }

                    string propertyName;
                    // If it was an identifier, use it as-is, otherwise strip the quotes
                    if (match.Groups["identifier"].Captures.Count > 0)
                    {
                        propertyName = match.Value;
                    }
                    else
                    {
                        propertyName = match.Value[1..^1];
                    }

                    if (string.IsNullOrEmpty(propertyName))
                    {
                        // It was an empty string; can't be a call.
                        break;
                    }

                    // Does it match a known parameter?
                    if (!parameters.TryGetValue(propertyName, out var propertyDefinition))
                    {
                        // Probably "arguments" or another ignorable string.
                        continue;
                    }

                    // Now look for the property value
                    match = propertyValueRegex.Match(rawMessageContent, offset);
                    offset = match.Index + match.Length;
                    if (!match.Success)
                    {
                        // No property value; can't be a legit call
                        break;
                    }

                    // Check if we captured anything of the right group (string, number, etc).
                    var groupName = GetRegexGroupName(propertyDefinition);
                    var targetGroup = match.Groups[groupName];

                    // Note that strings can also match the null literal.
                    if (targetGroup.Captures.Count == 0 && propertyDefinition.Type == "string")
                    {
                        targetGroup = match.Groups["null"];
                    }

                    if (targetGroup.Captures.Count == 0)
                    {
                        // Wasn't the correct type; bail
                        break;
                    }

                    var propertyValue = targetGroup.Value;

                    // For bools and null, convert to lower-case (we match case-insensitive because some models
                    // insist on using 'True' instead of 'true'... and they might do something similar for null)
                    if (targetGroup.Name is "boolean" or "null")
                    {
                        propertyValue = propertyValue.ToLowerInvariant();
                    }

                    arguments.Add($@"""{propertyName}""", propertyValue);
                }

                if (arguments.Count != parameters.Count)
                {
                    // Not enough args; try the next tool
                    // Note this assumes all args are required (could be relaxed)
                    continue;
                }

                // If we got here, the call was a success
                function = new FunctionCall
                {
                    Name = functionName,
                    Arguments = $$"""
                        { {{string.Join(", ", arguments.Select(i => i.Key + " : " + i.Value))}} }
                        """
                };

                return true;
            }

            // Didn't find anything that matched.
            return false;
        }

        static string GetRegexGroupName(PropertyDefinition property) => property.Type switch
        {
            "integer" => "number",
            _ => property.Type!
        };
    }
}
