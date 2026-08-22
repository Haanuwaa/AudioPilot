namespace AudioPilot.Cli
{
    internal static class CliDocumentationSync
    {
        private const string QuickReferenceHeading = "## Quick Reference";
        private const string QuickReferenceBoundary = "Use cases:";
        private const string AutomationRecipesHeading = "## Common Automation Recipes";
        private const string HelpTopicsHeading = "## Help Topics";
        private const string CommandsHeading = "## Commands";
        private const string CommandsBoundary = "Selector notes:";

        internal static string SyncCliGuide(string markdown)
        {
            string updated = ReplaceGeneratedBlock(markdown, QuickReferenceHeading, QuickReferenceBoundary, CliDocumentationMarkdownGenerator.GenerateQuickReferenceSection());
            updated = ReplaceWholeSection(updated, AutomationRecipesHeading, CliDocumentationMarkdownGenerator.GenerateAutomationRecipesSection());
            updated = ReplaceWholeSection(updated, HelpTopicsHeading, CliDocumentationMarkdownGenerator.GenerateHelpTopicsSection());
            updated = ReplaceGeneratedBlock(updated, CommandsHeading, CommandsBoundary, CliDocumentationMarkdownGenerator.GenerateCommandsSection());
            return NormalizeLineEndings(updated);
        }

        internal static bool IsCliGuideInSync(string markdown)
        {
            return string.Equals(NormalizeLineEndings(markdown), NormalizeLineEndings(SyncCliGuide(markdown)), StringComparison.Ordinal);
        }

        private static string ReplaceWholeSection(string markdown, string heading, string replacementSection)
        {
            (int sectionStart, int sectionEnd) = FindSectionBounds(markdown, heading);
            return markdown[..sectionStart] + ConcatWithBlankLine(replacementSection, markdown[sectionEnd..]);
        }

        private static string ReplaceGeneratedBlock(string markdown, string heading, string boundary, string replacementSection)
        {
            (int sectionStart, int sectionEnd) = FindSectionBounds(markdown, heading);
            string section = markdown[sectionStart..sectionEnd];
            int boundaryIndex = FindBoundaryIndex(section, boundary);
            if (boundaryIndex < 0)
            {
                return markdown[..sectionStart] + ConcatWithBlankLine(replacementSection, markdown[sectionEnd..]);
            }

            string suffix = section[boundaryIndex..];
            return markdown[..sectionStart] + ConcatWithBlankLine(replacementSection, suffix) + markdown[sectionEnd..];
        }

        private static (int Start, int End) FindSectionBounds(string markdown, string heading)
        {
            int headingIndex = markdown.IndexOf(heading, StringComparison.Ordinal);
            if (headingIndex < 0)
            {
                throw new InvalidOperationException($"Markdown section '{heading}' not found.");
            }

            int nextHeadingIndex = markdown.IndexOf("\n## ", headingIndex + heading.Length, StringComparison.Ordinal);
            int sectionEnd = nextHeadingIndex >= 0 ? nextHeadingIndex : markdown.Length;
            return (headingIndex, sectionEnd);
        }

        private static string NormalizeLineEndings(string value)
        {
            return value.Replace("\r\n", "\n", StringComparison.Ordinal);
        }

        private static int FindBoundaryIndex(string section, string boundaryHeading)
        {
            int boundaryIndex = section.IndexOf($"\n\n{boundaryHeading}", StringComparison.Ordinal);
            if (boundaryIndex >= 0)
            {
                return boundaryIndex + 1;
            }

            boundaryIndex = section.IndexOf($"\n{boundaryHeading}", StringComparison.Ordinal);
            return boundaryIndex >= 0 ? boundaryIndex + 1 : -1;
        }

        private static string ConcatWithBlankLine(string left, string right)
        {
            if (string.IsNullOrEmpty(right))
            {
                return left;
            }

            return left.TrimEnd() + "\n\n" + right.TrimStart('\r', '\n');
        }
    }
}
