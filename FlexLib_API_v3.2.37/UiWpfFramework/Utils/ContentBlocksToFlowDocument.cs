// ****************************************************************************
///*!	\file ContentBlocksToFlowDocument.cs
// *	\brief Convertes content blocks, which have text and URL, to 
// *           flowdocument to be properly converted on Maestro/M-models
// *
// *	\copyright	Copyright 2025 FlexRadio Systems.  All Rights Reserved.
// *				Unauthorized use, duplication or distribution of this software is
// *				strictly prohibited by law.
// */
// ****************************************************************************
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Media;

namespace Flex.UiWpfFramework.Utils
{
    public class ContentBlocksToFlowDocumentConverter : IValueConverter
    {
        private readonly IValueConverter _qrConverter = new UrlToQrConverter();

        // only these hosts (and their sub‑domains) are allowed
        private static readonly string[] AllowedHosts =
        {
            "flexradio.com",
            ".flexradio.com",
        };

        private static readonly Regex UrlPattern = new Regex(
            @"(?:\[[^\]]+\]\()?(https?://\S+)(?:\))?",
            RegexOptions.Compiled | RegexOptions.IgnoreCase
        );

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            IEnumerable<string> blocks = value as IEnumerable<string> ?? Enumerable.Empty<string>();
            var doc = new FlowDocument()
            {
                Background = Brushes.Transparent,
                FontSize = 32,
                Foreground = Brushes.White
            };

            // Split into content and links
            var contentLines = new List<string>();
            var linkInfos = new List<(string Caption, string Url)>();

            foreach (string block in blocks)
            {
                if (block.StartsWith("LINK|"))
                {
                    string[] parts = block.Split('|');
                    if (parts.Length == 3)
                        linkInfos.Add((parts[1], parts[2]));
                }
                else
                {
                    contentLines.Add(block);
                }
            }

            // Add text paragraphs first
            foreach (var line in contentLines)
            {
                var paragraph = new Paragraph(new Run(line))
                {
                    TextAlignment = TextAlignment.Center,
                    Margin = new Thickness(0, 0, 0, 8)
                };
                doc.Blocks.Add(paragraph);
            }

            // Add QR codes in a single horizontal row with borders
            if (linkInfos.Any())
            {
                var qrPanel = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Margin = new Thickness(0, 20, 0, 0)
                };

                foreach (var (caption, url) in linkInfos)
                {
                    if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) continue;

                    string host = uri.Host.ToLowerInvariant();
                    bool isAllowed = AllowedHosts.Any(allowed =>
                        host == allowed.TrimStart('.') || host.EndsWith(allowed, StringComparison.OrdinalIgnoreCase));
                    if (!isAllowed || uri.Scheme != Uri.UriSchemeHttps) continue;

                    ImageSource imgSource = _qrConverter.Convert(url, typeof(ImageSource), null, culture) as ImageSource;
                    if (imgSource == null) continue;

                    var captionBlock = new TextBlock
                    {
                        Text = caption,
                        FontSize = doc.FontSize,
                        Foreground = doc.Foreground,
                        TextAlignment = TextAlignment.Center,
                        Margin = new Thickness(0, 0, 0, 4),
                        Padding = new Thickness(0)
                    };

                    var img = new Image
                    {
                        Source = imgSource,
                        Width = 100,
                        Height = 100,
                    };

                    var box = new StackPanel
                    {
                        Orientation = Orientation.Vertical,
                        Children = { captionBlock, img }
                    };

                    var border = new Border
                    {
                        BorderBrush = Brushes.White,
                        BorderThickness = new Thickness(2),
                        CornerRadius = new CornerRadius(6),
                        Padding = new Thickness(6),
                        Margin = new Thickness(8, 0, 8, 0),
                        Child = box
                    };

                    qrPanel.Children.Add(border);
                }

                var qrContainer = new BlockUIContainer(qrPanel);
                doc.Blocks.Add(qrContainer);
            }

            return doc;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }
}
