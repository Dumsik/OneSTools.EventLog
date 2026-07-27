// Vendored from OneSTools.BracketsFile 2.1.9 (MIT, Akpaev Evgeniy, github.com/akpaevj/OneSTools.BracketsFile).
//
// ParseBlock used to run entirely against the StringBuilder produced by BracketsListReader.
// StringBuilder stores data as a chunk list (~8000 chars/chunk) and its indexer walks that list
// from the tail on every access, so a char-by-char scan of an already-buffered record is
// O(n^2/chunkSize), not O(n). A 70 MB event Comment (a single quoted field in one record) made
// this take ~20 hours instead of seconds. Fixed by materializing the record to a string once and
// scanning that (see ParseBlock(string, ...) and its string-based helpers below).
using System.Text;

namespace OneSTools.BracketsFile
{
    /// <summary>
    /// Represents static methods for working with 1C "brackets" data
    /// </summary>
    public static class BracketsParser
    {
        /// <summary>
        /// Returns parsed node
        /// </summary>
        /// <param name="text"></param>
        /// <param name="startIndex"></param>
        /// <param name="endIndex"></param>
        /// <returns></returns>
        public static BracketsNode ParseBlock(StringBuilder text, int startIndex = 0, int endIndex = -1)
        {
            // StringBuilder's indexer walks its chunk list from the tail on every access, so a full
            // char-by-char scan of an already-buffered record (e.g. one with a multi-megabyte Comment)
            // degrades to O(n^2). Materializing once to a string restores O(1) indexed access for the scan.
            return ParseBlock(text.ToString(), startIndex, endIndex);
        }

        /// <summary>
        /// Returns parsed node
        /// </summary>
        /// <param name="text"></param>
        /// <param name="startIndex"></param>
        /// <param name="endIndex"></param>
        /// <returns></returns>
        public static BracketsNode ParseBlock(string text, int startIndex = 0, int endIndex = -1)
        {
            var node = new BracketsNode();

            if (endIndex == -1)
                endIndex = GetNodeEndIndex(text, startIndex);
            if (endIndex == -1)
                endIndex = text.Length - 1;

            if (text[startIndex] == '{' && text[endIndex] == '}')
            {
                startIndex += 1;
                endIndex -= 1;
            }

            for (var i = startIndex; i <= endIndex; i++)
            {
                var currentChar = text[i];

                switch (currentChar)
                {
                    // string value
                    case '"':
                    {
                        var textValueBrackets = 0;
                        var valueEndIndex = GetTextValueEndIndex(text, i, ref textValueBrackets);
                        var value = text.Substring(i + 1, valueEndIndex - i - 1);
                        node.Nodes.Add(new BracketsNode(value));

                        i = valueEndIndex;
                        break;
                    }
                    // new block
                    case '{':
                    {
                        var valueEndIndex = GetNodeEndIndex(text, i);
                        var value = ParseBlock(text, i, valueEndIndex);
                        node.Nodes.Add(value);

                        i = valueEndIndex;
                        break;
                    }
                    default:
                    {
                        if (currentChar != '"' && currentChar != '}' && currentChar != ',' && !char.IsWhiteSpace(currentChar)) // another value
                        {
                            var valueEndIndex = GetValueEndIndex(text, i);
                            var value = text.Substring(i, valueEndIndex - i);
                            node.Nodes.Add(new BracketsNode(value));

                            i = valueEndIndex;
                        }

                        break;
                    }
                }
            }

            return node;
        }

        /// <summary>
        /// Returns the last index of the block. If the end hasn't been found than returns -1
        /// </summary>
        /// <param name="text"></param>
        /// <param name="startIndex"></param>
        /// <returns></returns>
        public static int GetNodeEndIndex(string text, int startIndex)
        {
            var quotes = 0;
            var brackets = 0;
            var textValueBrackets = 0;

            return GetNodeEndIndex(text, ref startIndex, ref quotes, ref brackets, ref textValueBrackets);
        }

        /// <summary>
        /// Returns the last index of the block and save counted key symbols in refs. If the end hasn't been found than returns -1
        /// </summary>
        /// <param name="text"></param>
        /// <param name="index"></param>
        /// <param name="quotes"></param>
        /// <param name="brackets"></param>
        /// <param name="textValueBrackets"></param>
        /// <returns></returns>
        internal static int GetNodeEndIndex(string text, ref int index, ref int quotes, ref int brackets, ref int textValueBrackets)
        {
            while (index < text.Length)
            {
                var prevChar = index > 0 ? text[index - 1] : '\0';
                var currentChar = text[index];

                if (prevChar == ',' && currentChar == '"')
                {
                    var textValueEndIndex = GetTextValueEndIndex(text, index, ref textValueBrackets);

                    if (textValueEndIndex == -1)
                    {
                        index = textValueEndIndex;
                        return index;
                    }

                    index = textValueEndIndex;
                    index++;
                    continue;
                }

                switch (currentChar)
                {
                    case '"':
                        quotes++;
                        break;
                    case '{':
                        brackets++;
                        break;
                    case '}':
                        brackets--;
                        break;
                }

                if (brackets == 0 && (quotes == 0 || (quotes != 0 && (quotes % 2) == 0)))
                    return index;

                index++;
            }

            return -1;
        }

        /// <summary>
        /// Returns the last index of the any value (except string value and block). If the end hasn't been found than returns -1
        /// </summary>
        /// <param name="text"></param>
        /// <param name="startIndex"></param>
        /// <returns></returns>
        public static int GetValueEndIndex(string text, int startIndex)
        {
            for (var i = startIndex; i < text.Length; i++)
            {
                var c = text[i];

                if (c == ',' || c == '}')
                    return i;
            }

            return -1;
        }

        /// <summary>
        /// Returns the last index of the text value. If the end hasn't been found than returns -1
        /// </summary>
        /// <param name="text"></param>
        /// <param name="startIndex"></param>
        /// <param name="textValueBrackets"></param>
        /// <returns></returns>
        public static int GetTextValueEndIndex(string text, int startIndex, ref int textValueBrackets)
        {
            for (var i = startIndex; i < text.Length; i++)
            {
                var prevChar = i > 0 ? text[i - 1] : '\0';
                var currentChar = text[i];
                var nextChar = text.Length > i + 1 ? text[i + 1] : '\0';

                if (currentChar == '"')
                    textValueBrackets++;

                if ((prevChar == '"' && (currentChar == ',' || currentChar == '}') || currentChar == '"' && (nextChar == ',' || nextChar == '}')) && (textValueBrackets == 0 || textValueBrackets % 2 == 0))
                    return i;
            }

            return -1;
        }

        // The overloads below operate directly on the StringBuilder that BracketsListReader is still
        // filling in from the stream. There, startIndex is always the index of the char just appended
        // (the StringBuilder's tail chunk), so each call only ever touches the last chunk and stays O(1) --
        // this incremental path was never quadratic, so it's left untouched.

        /// <summary>
        /// Returns the last index of the block and save counted key symbols in refs. If the end hasn't been found than returns -1
        /// </summary>
        /// <param name="text"></param>
        /// <param name="index"></param>
        /// <param name="quotes"></param>
        /// <param name="brackets"></param>
        /// <param name="textValueBrackets"></param>
        /// <returns></returns>
        internal static int GetNodeEndIndex(StringBuilder text, ref int index, ref int quotes, ref int brackets, ref int textValueBrackets)
        {
            while (index < text.Length)
            {
                var prevChar = index > 0 ? text[index - 1] : '\0';
                var currentChar = text[index];

                if (prevChar == ',' && currentChar == '"')
                {
                    var textValueEndIndex = GetTextValueEndIndex(text, index, ref textValueBrackets);

                    if (textValueEndIndex == -1)
                    {
                        index = textValueEndIndex;
                        return index;
                    }

                    index = textValueEndIndex;
                    index++;
                    continue;
                }

                switch (currentChar)
                {
                    case '"':
                        quotes++;
                        break;
                    case '{':
                        brackets++;
                        break;
                    case '}':
                        brackets--;
                        break;
                }

                if (brackets == 0 && (quotes == 0 || (quotes != 0 && (quotes % 2) == 0)))
                    return index;

                index++;
            }

            return -1;
        }

        /// <summary>
        /// Returns the last index of the text value. If the end hasn't been found than returns -1
        /// </summary>
        /// <param name="text"></param>
        /// <param name="startIndex"></param>
        /// <param name="textValueBrackets"></param>
        /// <returns></returns>
        public static int GetTextValueEndIndex(StringBuilder text, int startIndex, ref int textValueBrackets)
        {
            for (var i = startIndex; i < text.Length; i++)
            {
                var prevChar = i > 0 ? text[i - 1] : '\0';
                var currentChar = text[i];
                var nextChar = text.Length > i + 1 ? text[i + 1] : '\0';

                if (currentChar == '"')
                    textValueBrackets++;

                if ((prevChar == '"' && (currentChar == ',' || currentChar == '}') || currentChar == '"' && (nextChar == ',' || nextChar == '}')) && (textValueBrackets == 0 || textValueBrackets % 2 == 0))
                    return i;
            }

            return -1;
        }
    }
}
