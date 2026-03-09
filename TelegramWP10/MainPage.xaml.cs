// Внутри класса MainPage добавь эти словари и обнови ParseMessage:
private Dictionary<long, long> _fileToMsgId = new Dictionary<long, long>();
private Dictionary<long, MessageItem> _messagesDict = new Dictionary<long, MessageItem>();

private MessageItem ParseMessage(JToken msg) {
    try {
        long msgId = (long)msg["id"];
        string txt = "";
        string contentType = msg["content"]?["@type"]?.ToString();
        
        if (contentType == "messageText") txt = msg["content"]["text"]["text"]?.ToString();
        else txt = msg["content"]["caption"]?["text"]?.ToString() ?? "";

        var item = new MessageItem {
            Id = msgId, Text = txt,
            Date = DateTimeOffset.FromUnixTimeSeconds((long)msg["date"]).LocalDateTime.ToString("HH:mm"),
            Alignment = (bool)msg["is_outgoing"] ? HorizontalAlignment.Right : HorizontalAlignment.Left,
            Background = (bool)msg["is_outgoing"] ? "#0088cc" : "#333333"
        };

        if (msg["reply_to_message_id"]?.Value<long>() != 0) item.ReplyToText = "Ответ на сообщение";

        if (contentType == "messageVideo") {
            item.IsVideo = true;
            var thumb = msg["content"]["video"]?["thumbnail"]?["file"];
            if (thumb != null) ProcessMedia((long)thumb["id"], msgId, item);
        } else if (contentType == "messagePhoto") {
            var photo = msg["content"]["photo"]["sizes"].Last?["photo"];
            if (photo != null) ProcessMedia((long)photo["id"], msgId, item);
        }
        return item;
    } catch { return null; }
}

private void ProcessMedia(long fId, long msgId, MessageItem item) {
    _fileToMsgId[fId] = msgId;
    _messagesDict[msgId] = item;
    TdJson.SendUtf8(_client, "{\"@type\":\"downloadFile\",\"file_id\":" + fId + ",\"priority\":10}");
}

private async Task UpdateMessagePhoto(long msgId, string path) {
    await Task.Delay(300);
    await Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, async () => {
        try {
            var file = await StorageFile.GetFileFromPathAsync(path);
            using (var stream = await file.OpenReadAsync()) {
                var bitmap = new BitmapImage();
                await bitmap.SetSourceAsync(stream);
                if (_messagesDict.ContainsKey(msgId)) _messagesDict[msgId].AttachedPhoto = bitmap;
            }
        } catch { }
    });
}
