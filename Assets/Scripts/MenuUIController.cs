using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

// ATOMIC DEPLOY CHECK: lobby menu bundle 2026-06-29 protocol v2.
public class MenuUIController : MonoBehaviour
{
    [SerializeField] private GameObject findServerPanel;
    [SerializeField] private Button findServerButton;
    [SerializeField] private Button refreshServerButton;
    [SerializeField] private GameObject createServerPanel;
    [SerializeField] private Button createServerButton;
    [SerializeField] private DynamicTcpClient tcpClient;

    private const int MaxAllowedPlayers = 12;

    private Button createServerSubmitButton;
    private Slider maxPlayersSlider;
    private TMP_Text maxPlayersValueText;
    private TMP_InputField lobbyNameInput;
    private GameObject serverRowTemplate;
    private Transform serverRowParent;
    private readonly Dictionary<int, ServerListingInfo> serverListings = new();
    private readonly Dictionary<int, ServerListingInfo> pendingServerListings = new();
    private readonly List<GameObject> serverRows = new();
    private bool receivingListingSnapshot;
    private bool requestedInitialListings;
    private bool waitingForCreateResponse;
    private bool receivedServerProtocol;
    private int createServerPanelShownFrame = -1;
    private int createServerListingClickFrame = -1;

    private void Start()
    {
        if (findServerPanel == null)
        {
            findServerPanel = FindFirstGameObject("FindServerPanel", "FindServerMockup");
        }

        if (createServerPanel == null)
        {
            createServerPanel = FindCreateServerPanel();
        }

        createServerPanel = ResolveCreateServerPanel(createServerPanel);

        if (tcpClient == null)
        {
            tcpClient = FindOrCreateTcpClient();
        }

        if (tcpClient != null)
        {
            tcpClient.MessageReceived += HandleTcpMessage;
        }

        if (findServerButton == null)
        {
            GameObject buttonObject = GameObject.Find("FindaServerButton");
            findServerButton = buttonObject != null ? buttonObject.GetComponent<Button>() : null;
        }

        if (createServerButton == null)
        {
            createServerButton = FindButton("CreateaServerButton", "Create a Server");
        }

        if (refreshServerButton == null)
        {
            refreshServerButton = FindButton("RefreshButton", "Refresh");
        }

        createServerSubmitButton = FindOrCreateSubmitButton();
        ConfigureMaxPlayersSlider();
        lobbyNameInput = FindLobbyNameInput();
        ConfigureServerListTemplate();

        if (findServerButton != null)
        {
            AddListenerIfMissing(findServerButton, nameof(ShowFindServerPanel), ShowFindServerPanel);
        }

        if (createServerButton != null)
        {
            AddListenerIfMissing(createServerButton, nameof(ShowCreateServerPanel), ShowCreateServerPanel);
        }

        if (createServerSubmitButton != null)
        {
            createServerSubmitButton.onClick.AddListener(CreateServerListing);
        }

        if (refreshServerButton != null)
        {
            AddListenerIfMissing(refreshServerButton, nameof(RequestServerListings), RequestServerListings);
        }

    }

    private void Update()
    {
        if (!requestedInitialListings && tcpClient != null && tcpClient.IsConnected)
        {
            requestedInitialListings = true;
            RequestServerListings();
        }
    }

    public void ShowFindServerPanel()
    {
        if (findServerPanel != null)
        {
            bool shouldShow = !findServerPanel.activeSelf;
            findServerPanel.SetActive(shouldShow);

            if (shouldShow)
            {
                HideCreateServerPanel();
                RequestServerListings();
            }
        }
    }

    public void HideFindServerPanel()
    {
        if (findServerPanel != null)
        {
            findServerPanel.SetActive(false);
        }
    }

    public void ShowCreateServerPanel()
    {
        if (createServerPanel == null)
        {
            createServerPanel = FindCreateServerPanel();
        }

        createServerPanel = ResolveCreateServerPanel(createServerPanel);

        if (createServerPanel != null)
        {
            bool shouldShow = !createServerPanel.activeSelf;
            createServerPanel.SetActive(shouldShow);

            if (shouldShow)
            {
                HideFindServerPanel();
                createServerPanelShownFrame = Time.frameCount;
            }
        }
        else
        {
            Debug.LogWarning("Create Server UI was not found.");
        }
    }

    public void CreateServerListing()
    {
        if (createServerListingClickFrame == Time.frameCount)
        {
            return;
        }

        createServerListingClickFrame = Time.frameCount;

        if (createServerPanel == null || !createServerPanel.activeSelf)
        {
            ShowCreateServerPanel();
            return;
        }

        if (createServerPanelShownFrame == Time.frameCount)
        {
            return;
        }

        if (tcpClient == null)
        {
            tcpClient = FindOrCreateTcpClient();
        }

        if (tcpClient == null)
        {
            Debug.LogWarning("Cannot create server listing because no DynamicTcpClient was found.");
            return;
        }

        int maxPlayers = GetSelectedMaxPlayers();
        string lobbyName = GetLobbyName();
        Debug.Log($"Creating server listing '{lobbyName}' ({maxPlayers} players).");
        _ = CreateServerListingFlowAsync(maxPlayers, lobbyName);
    }

    private async Task CreateServerListingFlowAsync(int maxPlayers, string lobbyName)
    {
        waitingForCreateResponse = true;
        Task createTask = tcpClient.CreateServerListingAsync(maxPlayers, lobbyName);
        Task completedTask = await Task.WhenAny(createTask, Task.Delay(2000));

        if (completedTask != createTask)
        {
            Debug.LogWarning("Server listing create request did not finish quickly. Refreshing the server list anyway.");
        }

        RequestServerListings();
        await Task.Delay(3000);

        if (waitingForCreateResponse)
        {
            Debug.LogWarning(receivedServerProtocol
                ? "No server listing response received."
                : "The connected dedicated server is not running the latest lobby-listing build.");
            waitingForCreateResponse = false;
        }
    }

    private async Task CheckServerProtocolAsync()
    {
        await Task.Delay(2000);

        if (!receivedServerProtocol)
        {
            Debug.LogWarning("Connected server did not report lobby protocol v2. Rebuild and redeploy the dedicated server binary.");
        }
    }

    public void HideCreateServerPanel()
    {
        if (createServerPanel != null)
        {
            createServerPanel.SetActive(false);
        }
    }

    private async void RequestServerListings()
    {
        if (tcpClient == null)
        {
            tcpClient = FindOrCreateTcpClient();
        }

        if (tcpClient == null)
        {
            Debug.LogWarning("Cannot request server listings because no DynamicTcpClient was found.");
            return;
        }

        if (!tcpClient.IsConnected)
        {
            await tcpClient.ConnectAsync();
        }

        await tcpClient.SendAsync("listings_request");
    }

    private void HandleTcpMessage(string message)
    {
        string[] parts = message.Split('|');
        if (parts.Length == 0)
        {
            return;
        }

        parts[0] = parts[0].Trim().Trim('\uFEFF');

        if (parts[0] == "welcome")
        {
            requestedInitialListings = true;
            RequestServerListings();
            _ = CheckServerProtocolAsync();
            return;
        }

        if (parts[0] == "server_protocol")
        {
            receivedServerProtocol = parts.Length > 1 && parts[1] == "2";
            return;
        }

        if (parts[0] == "listings_begin")
        {
            receivingListingSnapshot = true;
            pendingServerListings.Clear();
            return;
        }

        if (parts[0] == "listing" || parts[0] == "listing_added" || parts[0] == "listing_created")
        {
            waitingForCreateResponse = false;

            if (TryParseListing(parts, out ServerListingInfo listing))
            {
                if (receivingListingSnapshot)
                {
                    pendingServerListings[listing.Id] = listing;
                    return;
                }

                serverListings[listing.Id] = listing;
                RebuildServerRows();
            }

            return;
        }

        if (parts[0] == "listings_end")
        {
            receivingListingSnapshot = false;
            waitingForCreateResponse = false;

            serverListings.Clear();

            foreach (KeyValuePair<int, ServerListingInfo> listing in pendingServerListings)
            {
                serverListings[listing.Key] = listing.Value;
            }

            RebuildServerRows();
            pendingServerListings.Clear();
            return;
        }

        if (parts[0] == "listing_removed" && parts.Length > 1 && int.TryParse(parts[1], out int listingId))
        {
            serverListings.Remove(listingId);
            RebuildServerRows();
            return;
        }

        if (parts[0] != "join" && parts[0] != "leave" && parts[0] != "state")
        {
            Debug.LogWarning($"Unhandled TCP menu message: {message}");
        }
    }

    private bool TryParseListing(string[] parts, out ServerListingInfo listing)
    {
        listing = default;

        if (parts.Length < 5 ||
            !int.TryParse(parts[1], out int id) ||
            !int.TryParse(parts[2], out int hostClientId) ||
            !int.TryParse(parts[3], out int maxPlayers) ||
            !int.TryParse(parts[4], out int currentPlayers))
        {
            return false;
        }

        string name = parts.Length > 5 && !string.IsNullOrWhiteSpace(parts[5]) ? parts[5] : $"Server {id}";
        listing = new ServerListingInfo(id, hostClientId, maxPlayers, currentPlayers, name);
        return true;
    }

    private void ConfigureServerListTemplate()
    {
        serverRowTemplate = FindFirstGameObject("Server Row 1", "ServerName");
        if (serverRowTemplate == null)
        {
            Debug.LogWarning("Server Row 1 template was not found. Active server listings cannot be drawn.");
            return;
        }

        serverRowParent = serverRowTemplate.transform.parent;
        serverRowTemplate.SetActive(false);
        RebuildServerRows();
    }

    private void RebuildServerRows()
    {
        if (serverRowTemplate == null || serverRowParent == null)
        {
            return;
        }

        foreach (GameObject row in serverRows)
        {
            if (row != null)
            {
                row.SetActive(false);
                Destroy(row);
            }
        }

        serverRows.Clear();

        List<int> listingIds = new(serverListings.Keys);
        listingIds.Sort();

        foreach (int listingId in listingIds)
        {
            ServerListingInfo listing = serverListings[listingId];
            GameObject row = Instantiate(serverRowTemplate, serverRowParent);
            row.name = $"Server Row {listing.Id}";
            row.SetActive(true);
            ApplyServerRowText(row, listing);
            serverRows.Add(row);
        }

        Debug.Log($"Server list updated: {serverRows.Count} active.");
    }

    private void ApplyServerRowText(GameObject row, ServerListingInfo listing)
    {
        TMP_Text nameText = FindTextUnder(row.transform, "NameOfServer");
        if (nameText != null)
        {
            nameText.text = listing.Name;
        }

        Button joinButton = row.GetComponentInChildren<Button>(true);
        TMP_Text joinText = joinButton != null ? joinButton.GetComponentInChildren<TMP_Text>(true) : FindTextUnder(row.transform, "JoinLobbyButton");
        if (joinText != null)
        {
            joinText.text = $"{listing.CurrentPlayers}/{listing.MaxPlayers} Join";
        }
    }

    private TMP_Text FindTextUnder(Transform root, string objectName)
    {
        Transform child = FindChildNamed(root, objectName);
        return child != null ? child.GetComponentInChildren<TMP_Text>(true) : null;
    }

    private string GetLobbyName()
    {
        if (lobbyNameInput == null)
        {
            lobbyNameInput = FindLobbyNameInput();
        }

        if (lobbyNameInput != null && !string.IsNullOrWhiteSpace(lobbyNameInput.text))
        {
            return lobbyNameInput.text.Trim();
        }

        return "Server";
    }

    private TMP_InputField FindLobbyNameInput()
    {
        if (createServerPanel == null)
        {
            return null;
        }

        Transform inputTransform = FindChildNamed(createServerPanel.transform, "LobbyNameInput");
        if (inputTransform != null)
        {
            TMP_InputField input = inputTransform.GetComponent<TMP_InputField>();
            if (input != null)
            {
                return input;
            }
        }

        return createServerPanel.GetComponentInChildren<TMP_InputField>(true);
    }

    private void AddListenerIfMissing(Button button, string methodName, UnityEngine.Events.UnityAction action)
    {
        for (int i = 0; i < button.onClick.GetPersistentEventCount(); i++)
        {
            if (button.onClick.GetPersistentMethodName(i) == methodName)
            {
                return;
            }
        }

        button.onClick.AddListener(action);
    }

    private GameObject FindGameObjectEvenIfInactive(string objectName)
    {
        GameObject[] objects = Resources.FindObjectsOfTypeAll<GameObject>();

        foreach (GameObject obj in objects)
        {
            if (obj.name == objectName && obj.scene.IsValid() && obj.scene.isLoaded)
            {
                return obj;
            }
        }

        return null;
    }

    private GameObject FindFirstGameObject(params string[] objectNames)
    {
        foreach (string objectName in objectNames)
        {
            GameObject obj = FindGameObjectEvenIfInactive(objectName);
            if (obj != null)
            {
                return obj;
            }
        }

        return null;
    }

    private GameObject FindCreateServerPanel()
    {
        GameObject panel = FindFirstGameObject("Create Server UI", "CreateServerUI", "CreateServerPanel", "Create Server");
        if (panel != null)
        {
            return panel;
        }

        GameObject maxPlayers = FindFirstGameObject("MaxPlayers", "Max Players");
        if (maxPlayers != null)
        {
            Transform createServerUi = FindAncestorNamed(maxPlayers.transform, "Create Server UI");
            if (createServerUi != null)
            {
                return createServerUi.gameObject;
            }

            Transform createServer = FindAncestorNamed(maxPlayers.transform, "Create Server");
            return createServer != null ? createServer.gameObject : maxPlayers;
        }

        return FindCreateServerPanelFromSlider();
    }

    private GameObject ResolveCreateServerPanel(GameObject panel)
    {
        if (panel == null)
        {
            return null;
        }

        Transform createServerUi = FindAncestorNamed(panel.transform, "Create Server UI");
        if (createServerUi != null)
        {
            return createServerUi.gameObject;
        }

        Transform createServerPanelObject = FindAncestorNamed(panel.transform, "CreateServerUI");
        if (createServerPanelObject != null)
        {
            return createServerPanelObject.gameObject;
        }

        return panel;
    }

    private Button FindOrCreateSubmitButton()
    {
        if (createServerPanel == null)
        {
            return null;
        }

        Button existingButton = FindButtonInPanel("Create Server");
        if (existingButton != null && existingButton != createServerButton)
        {
            return existingButton;
        }

        Transform submitTransform = FindChildNamed(createServerPanel.transform, "Create Server");
        if (submitTransform == null)
        {
            return null;
        }

        Button submitButton = submitTransform.GetComponent<Button>();
        if (submitButton == null)
        {
            submitButton = submitTransform.gameObject.AddComponent<Button>();
        }

        Graphic targetGraphic = submitTransform.GetComponent<Graphic>();
        if (targetGraphic == null)
        {
            Image image = submitTransform.gameObject.AddComponent<Image>();
            image.color = new Color(1f, 1f, 1f, 0f);
            targetGraphic = image;
        }

        targetGraphic.raycastTarget = true;
        submitButton.targetGraphic = targetGraphic;
        submitButton.interactable = true;

        return submitButton;
    }

    private Button FindButtonInPanel(string label)
    {
        if (createServerPanel == null)
        {
            return null;
        }

        Button[] buttons = createServerPanel.GetComponentsInChildren<Button>(true);

        foreach (Button button in buttons)
        {
            TMP_Text text = button.GetComponentInChildren<TMP_Text>(true);
            if (text != null && text.text == label)
            {
                return button;
            }
        }

        return null;
    }

    private Button FindButton(string objectName, string label)
    {
        GameObject buttonObject = GameObject.Find(objectName);
        Button button = buttonObject != null ? buttonObject.GetComponent<Button>() : null;

        return button != null ? button : FindButtonByLabel(label);
    }

    private int GetSelectedMaxPlayers()
    {
        Slider slider = maxPlayersSlider != null ? maxPlayersSlider : FindMaxPlayersSlider();
        return slider != null ? Mathf.Clamp(Mathf.RoundToInt(slider.value), 1, MaxAllowedPlayers) : 8;
    }

    private void ConfigureMaxPlayersSlider()
    {
        maxPlayersSlider = FindMaxPlayersSlider();
        maxPlayersValueText = FindMaxPlayersValueText();

        if (maxPlayersSlider == null)
        {
            Debug.LogWarning("Max Players slider was not found.");
            return;
        }

        maxPlayersSlider.minValue = 1;
        maxPlayersSlider.maxValue = MaxAllowedPlayers;
        maxPlayersSlider.wholeNumbers = true;
        maxPlayersSlider.SetValueWithoutNotify(Mathf.Clamp(Mathf.RoundToInt(maxPlayersSlider.value), 1, MaxAllowedPlayers));
        UpdateMaxPlayersValue(maxPlayersSlider.value);
        maxPlayersSlider.onValueChanged.RemoveListener(UpdateMaxPlayersValue);
        maxPlayersSlider.onValueChanged.AddListener(UpdateMaxPlayersValue);

    }

    private void UpdateMaxPlayersValue(float value)
    {
        int maxPlayers = Mathf.Clamp(Mathf.RoundToInt(value), 1, MaxAllowedPlayers);

        if (maxPlayersSlider != null && !Mathf.Approximately(maxPlayersSlider.value, maxPlayers))
        {
            maxPlayersSlider.SetValueWithoutNotify(maxPlayers);
        }

        if (maxPlayersValueText != null)
        {
            maxPlayersValueText.text = maxPlayers.ToString();
        }
    }

    private Slider FindMaxPlayersSlider()
    {
        Transform maxPlayersRoot = FindMaxPlayersRoot();
        return maxPlayersRoot != null ? maxPlayersRoot.GetComponentInChildren<Slider>(true) : null;
    }

    private TMP_Text FindMaxPlayersValueText()
    {
        Transform maxPlayersRoot = FindMaxPlayersRoot();
        if (maxPlayersRoot == null)
        {
            return null;
        }

        Transform valueTransform = FindChildNamed(maxPlayersRoot, "Value");
        if (valueTransform != null)
        {
            TMP_Text valueText = valueTransform.GetComponentInChildren<TMP_Text>(true);
            if (valueText != null)
            {
                return valueText;
            }
        }

        foreach (TMP_Text text in maxPlayersRoot.GetComponentsInChildren<TMP_Text>(true))
        {
            if (int.TryParse(text.text, out _))
            {
                return text;
            }
        }

        return null;
    }

    private Transform FindMaxPlayersRoot()
    {
        if (createServerPanel == null)
        {
            return null;
        }

        Transform maxPlayersRoot = FindChildNamed(createServerPanel.transform, "MaxPlayers");
        if (maxPlayersRoot != null)
        {
            return maxPlayersRoot;
        }

        return FindChildNamed(createServerPanel.transform, "Max Players");
    }

    private GameObject FindCreateServerPanelFromSlider()
    {
        Slider[] sliders = Resources.FindObjectsOfTypeAll<Slider>();

        foreach (Slider slider in sliders)
        {
            if (!slider.gameObject.scene.IsValid() || !slider.gameObject.scene.isLoaded)
            {
                continue;
            }

            Transform panel = FindAncestorNamed(slider.transform, "MaxPlayers");
            if (panel != null)
            {
                return panel.gameObject;
            }

            return slider.transform.parent != null ? slider.transform.parent.gameObject : slider.gameObject;
        }

        return null;
    }

    private DynamicTcpClient FindOrCreateTcpClient()
    {
        DynamicTcpClient[] clients = Resources.FindObjectsOfTypeAll<DynamicTcpClient>();

        foreach (DynamicTcpClient client in clients)
        {
            if (client.gameObject.scene.IsValid() && client.gameObject.scene.isLoaded)
            {
                return client;
            }
        }

        return gameObject.AddComponent<DynamicTcpClient>();
    }

    private Transform FindAncestorNamed(Transform transform, string objectName)
    {
        while (transform != null)
        {
            if (transform.name == objectName)
            {
                return transform;
            }

            transform = transform.parent;
        }

        return null;
    }

    private Transform FindChildNamed(Transform parent, string objectName)
    {
        foreach (Transform child in parent.GetComponentsInChildren<Transform>(true))
        {
            if (child.name == objectName)
            {
                return child;
            }
        }

        return null;
    }

    private string GetHierarchyPath(Transform transform)
    {
        string path = transform.name;

        while (transform.parent != null)
        {
            transform = transform.parent;
            path = $"{transform.name}/{path}";
        }

        return path;
    }

    private Button FindButtonByLabel(string label)
    {
        Button[] buttons = Resources.FindObjectsOfTypeAll<Button>();

        foreach (Button button in buttons)
        {
            if (!button.gameObject.scene.IsValid() || !button.gameObject.scene.isLoaded)
            {
                continue;
            }

            TMP_Text text = button.GetComponentInChildren<TMP_Text>(true);
            if (text != null && text.text == label)
            {
                return button;
            }
        }

        return null;
    }

    private void OnDestroy()
    {
        if (tcpClient != null)
        {
            tcpClient.MessageReceived -= HandleTcpMessage;
        }
    }

    private readonly struct ServerListingInfo
    {
        public ServerListingInfo(int id, int hostClientId, int maxPlayers, int currentPlayers, string name)
        {
            Id = id;
            HostClientId = hostClientId;
            MaxPlayers = maxPlayers;
            CurrentPlayers = currentPlayers;
            Name = name;
        }

        public int Id { get; }
        public int HostClientId { get; }
        public int MaxPlayers { get; }
        public int CurrentPlayers { get; }
        public string Name { get; }
    }

}
