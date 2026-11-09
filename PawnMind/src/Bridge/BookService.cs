using System;
using System.Collections.Generic;
using System.Text;
using TMPro;
using UnityEngine;
using Newtonsoft.Json;
using PawnMind.Core;
using PawnMind.Game;

namespace PawnMind.Bridge
{
    internal static class BookService
    {
        public static string ListJson()
        {
            var books = new List<BookEntry>();
            List<string> containers = GameState.GetContainerIds();
            for (int c = 0; c < containers.Count; c++)
            {
                GameInventory inventory = GameState.GetContainer(containers[c]);
                if (inventory == null) continue;

                List<GameItem> items = inventory.childItems;
                for (int i = 0; i < items.Count; i++)
                {
                    GameItem item = items[i];
                    if (item == null || !GameState.IsOwned(item)) continue;

                    int multiPage = MultiPageIndex(item.identifier);
                    if (multiPage >= 0)
                    {
                        books.Add(new BookEntry
                        {
                            ItemId = item.identifier,
                            BookId = item.identifier,
                            Title = item.name,
                            Kind = "multipage",
                            PageCount = MultiPageCount(multiPage)
                        });
                    }
                    else if (SafeTag(item, "BOOK_TAG"))
                    {
                        string bookId = BookId(item);
                        books.Add(new BookEntry
                        {
                            ItemId = item.identifier,
                            BookId = bookId,
                            Title = item.name,
                            Kind = "vanilla",
                            PageCount = PageCount(bookId)
                        });
                    }
                    else if (SafeTag(item, "GUIDE_PAPER_TAG"))
                    {
                        multiPage = MultiPageIndex(item.identifier);
                        PaperBookRegistry.Entry entry = multiPage >= 0 ? null : PaperEntry(item.identifier);
                        books.Add(new BookEntry
                        {
                            ItemId = item.identifier,
                            BookId = entry == null ? item.identifier : (entry.bookId ?? item.identifier),
                            Title = item.name,
                            Kind = multiPage >= 0 ? "multipage" : "paper",
                            PageCount = multiPage >= 0 ? MultiPageCount(multiPage) : (entry == null || entry.data == null ? 0 : entry.data.pages.Count)
                        });
                    }
                }
            }

            var rows = new List<Dictionary<string, object>>();
            for (int i = 0; i < books.Count; i++)
            {
                rows.Add(new Dictionary<string, object>
                {
                    { "item_id", books[i].ItemId },
                    { "book_id", books[i].BookId },
                    { "title", books[i].Title },
                    { "kind", books[i].Kind },
                    { "page_count", books[i].PageCount }
                });
            }

            return JsonConvert.SerializeObject(new Dictionary<string, object>
            {
                { "ok", true },
                { "books", rows }
            });
        }

        public static string ReadJson(string key, int page)
        {
            if (string.IsNullOrEmpty(key)) return "{\"ok\":false,\"error\":\"missing_book_id\"}";
            if (!IsOwned(key)) return "{\"ok\":false,\"error\":\"not_unlocked\"}";

            int multiPage = MultiPageIndex(key);
            if (multiPage >= 0) return MultiPageJson(key, multiPage, page);

            StoreBookData vanilla = Vanilla(key);
            if (vanilla != null) return VanillaJson(key, vanilla, page);

            PaperBookRegistry.Entry entry = PaperEntry(key);
            if (entry == null) entry = PaperBookRegistry.Resolve(key);
            if (entry != null && entry.data != null) return PaperJson(entry, page);

            return "{\"ok\":false,\"error\":\"book_not_found\"}";
        }

        public static string ReadItemJson(int itemId)
        {
            string baseJson = InventoryService.GetItemJson(itemId);
            GameItem item = GameState.FindItemAnywhere(itemId);
            if (item == null) return baseJson;

            var payload = JsonConvert.DeserializeObject<Dictionary<string, object>>(baseJson);
            payload["book_tag"] = SafeTag(item, "BOOK_TAG");
            payload["guide_paper_tag"] = SafeTag(item, "GUIDE_PAPER_TAG");
            payload["important"] = SafeTag(item, "IMPORTANT_TAG");
            if (payload["book_tag"] is bool && (bool)payload["book_tag"]) payload["book_id"] = BookId(item);
            return JsonConvert.SerializeObject(payload);
        }

        private static string VanillaJson(string key, StoreBookData data, int page)
        {
            var pages = new List<Dictionary<string, object>>();
            int count = data.pageCount;
            for (int i = 0; i < count; i++)
            {
                var row = new Dictionary<string, object>();
                row["index"] = i;
                row["subtitle"] = data.subtitle != null && i < data.subtitle.Length ? data.subtitle[i] : "";
                row["text"] = data.body != null && i < data.body.Length ? data.body[i] : "";
                pages.Add(row);
            }

            var payload = new Dictionary<string, object>();
            payload["ok"] = true;
            payload["book_id"] = key;
            payload["title"] = data.title;
            payload["kind"] = "vanilla";
            payload["pages"] = pages;
            payload["page"] = page >= 0 && page < pages.Count ? pages[page] : null;
            return JsonConvert.SerializeObject(payload);
        }

        private static string PaperJson(PaperBookRegistry.Entry entry, int page)
        {
            PaperBookData data = entry.data;
            var pages = new List<Dictionary<string, object>>();
            for (int i = 0; i < data.pages.Count; i++)
            {
                PaperPageData source = data.pages[i];
                var row = new Dictionary<string, object>();
                row["index"] = i;
                row["name"] = source.name;

                var text = new StringBuilder();
                for (int e = 0; e < source.elements.Count; e++)
                {
                    PaperElementData element = source.elements[e];
                    if (element == null) continue;
                    string value = !string.IsNullOrEmpty(element.text) ? element.text : element.loc;
                    if (string.IsNullOrEmpty(value)) continue;
                    if (text.Length > 0) text.Append("\n");
                    text.Append(value);
                }

                row["text"] = text.ToString();
                pages.Add(row);
            }

            var payload = new Dictionary<string, object>();
            payload["ok"] = true;
            payload["book_id"] = entry.bookId;
            payload["title"] = string.IsNullOrEmpty(data.title) ? entry.Title : data.title;
            payload["kind"] = "paper";
            payload["pages"] = pages;
            payload["page"] = page >= 0 && page < pages.Count ? pages[page] : null;
            return JsonConvert.SerializeObject(payload);
        }

        private static int MultiPageIndex(string identifier)
        {
            switch ((identifier ?? string.Empty).ToLowerInvariant())
            {
                case "fuel_guide": return 0;
                case "water_guide": return 1;
                case "hydroponic_guide": return 2;
                case "chemistry_guide": return 3;
                case "ribwich_guide": return 4;
                case "ingot_book": return 5;
                case "hand_guide": return 6;
                case "wine_guide": return 7;
                default: return -1;
            }
        }

        private static int MultiPageCount(int index)
        {
            try
            {
                MultiPageUIManager manager = MultiPageUIManager.Instance;
                if (manager == null || manager.books == null || index < 0 || index >= manager.books.Count) return 0;
                return manager.books[index] == null ? 0 : manager.books[index].transform.childCount;
            }
            catch
            {
                return 0;
            }
        }

        private static string MultiPageJson(string key, int index, int page)
        {
            var pages = new List<Dictionary<string, object>>();
            try
            {
                MultiPageUIManager manager = MultiPageUIManager.Instance;
                if (manager != null && manager.books != null && index >= 0 && index < manager.books.Count)
                {
                    GameObject book = manager.books[index];
                    for (int i = 0; i < book.transform.childCount; i++)
                    {
                        Transform child = book.transform.GetChild(i);
                        var text = new StringBuilder();
                        TextMeshProUGUI[] labels = child.GetComponentsInChildren<TextMeshProUGUI>(true);
                        for (int l = 0; l < labels.Length; l++)
                        {
                            if (labels[l] == null || string.IsNullOrEmpty(labels[l].text)) continue;
                            if (text.Length > 0) text.Append("\n");
                            text.Append(labels[l].text);
                        }

                        var row = new Dictionary<string, object>();
                        row["index"] = i;
                        row["name"] = child.name;
                        row["text"] = text.ToString();
                        pages.Add(row);
                    }
                }
            }
            catch
            {
            }

            var payload = new Dictionary<string, object>();
            payload["ok"] = true;
            payload["book_id"] = key;
            payload["title"] = key;
            payload["kind"] = "multipage";
            payload["pages"] = pages;
            payload["page"] = page >= 0 && page < pages.Count ? pages[page] : null;
            return JsonConvert.SerializeObject(payload);
        }

        private static bool IsOwned(string key)
        {
            List<string> containers = GameState.GetContainerIds();
            for (int c = 0; c < containers.Count; c++)
            {
                GameInventory inventory = GameState.GetContainer(containers[c]);
                if (inventory == null) continue;

                List<GameItem> items = inventory.childItems;
                for (int i = 0; i < items.Count; i++)
                {
                    GameItem item = items[i];
                    if (item == null || !GameState.IsOwned(item)) continue;
                    if (string.Equals(item.identifier, key, StringComparison.OrdinalIgnoreCase)) return true;
                    if (SafeTag(item, "BOOK_TAG")
                        && string.Equals(BookId(item), key, StringComparison.OrdinalIgnoreCase)) return true;
                }
            }

            return false;
        }

        private static StoreBookData Vanilla(string key)
        {
            try
            {
                if (StoreBookData.bookDatas != null && StoreBookData.bookDatas.ContainsKey(key))
                    return StoreBookData.GetBookById(key);
            }
            catch
            {
            }

            return null;
        }

        private static int PageCount(string bookId)
        {
            StoreBookData data = Vanilla(bookId);
            if (data != null) return data.pageCount;
            return 0;
        }

        private static string BookId(GameItem item)
        {
            try
            {
                return item.GetTagReadonly("BOOK_ID").GetString();
            }
            catch
            {
                return null;
            }
        }

        private static bool SafeTag(GameItem item, string tag)
        {
            try { return item.IsTag(tag); }
            catch { return false; }
        }

        private static PaperBookRegistry.Entry PaperEntry(string itemId)
        {
            try
            {
                foreach (PaperBookRegistry.Entry entry in PaperBookRegistry.All)
                {
                    if (entry == null || entry.data == null || entry.data.item == null) continue;
                    if (string.Equals(entry.data.item.id, itemId, StringComparison.OrdinalIgnoreCase)) return entry;
                }
            }
            catch
            {
            }

            return null;
        }
    }
}
