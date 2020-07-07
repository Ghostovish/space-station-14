﻿#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using Content.Server.GameObjects.Components.Interactable;
using Content.Server.GameObjects.Components.VendingMachines;
using Content.Server.GameObjects.EntitySystems.Click;
using Content.Server.Interfaces.GameObjects.Components.Interaction;
using Content.Server.Interfaces;
using Content.Server.Interfaces.GameObjects;
using Content.Shared.GameObjects.Components;
using Content.Shared.GameObjects.Components.Interactable;
using Content.Shared.GameObjects.EntitySystems;
using JetBrains.Annotations;
using Robust.Server.GameObjects;
using Robust.Server.GameObjects.Components.UserInterface;
using Robust.Server.GameObjects.EntitySystems;
using Robust.Server.Interfaces.GameObjects;
using Robust.Server.Interfaces.Player;
using Robust.Shared.GameObjects;
using Robust.Shared.GameObjects.Systems;
using Robust.Shared.Interfaces.Random;
using Robust.Shared.IoC;
using Robust.Shared.Localization;
using Robust.Shared.Random;
using Robust.Shared.Serialization;
using Robust.Shared.Utility;
using Robust.Shared.ViewVariables;

namespace Content.Server.GameObjects.Components
{
    [RegisterComponent]
    public class WiresComponent : SharedWiresComponent, IInteractUsing, IExamine, IMapInit
    {
#pragma warning disable 649
        [Dependency] private readonly IRobustRandom _random = default!;
        [Dependency] private readonly IServerNotifyManager _notifyManager = default!;
#pragma warning restore 649
        private AudioSystem _audioSystem = default!;
        private AppearanceComponent _appearance = default!;
        private BoundUserInterface _userInterface = default!;

        private bool _isPanelOpen;

        /// <summary>
        /// Opening the maintenance panel (typically with a screwdriver) changes this.
        /// </summary>
        [ViewVariables]
        public bool IsPanelOpen
        {
            get => _isPanelOpen;
            private set
            {
                if (_isPanelOpen == value)
                {
                    return;
                }

                _isPanelOpen = value;
                UpdateAppearance();
            }
        }

        private bool _isPanelVisible = true;

        /// <summary>
        /// Components can set this to prevent the maintenance panel overlay from showing even if it's open
        /// </summary>
        [ViewVariables]
        public bool IsPanelVisible
        {
            get => _isPanelVisible;
            set
            {
                if (_isPanelVisible == value)
                {
                    return;
                }

                _isPanelVisible = value;
                UpdateAppearance();
            }
        }

        [ViewVariables(VVAccess.ReadWrite)]
        public string BoardName
        {
            get => _boardName;
            set
            {
                _boardName = value;
                UpdateUserInterface();
            }
        }

        [ViewVariables(VVAccess.ReadWrite)]
        public string? SerialNumber
        {
            get => _serialNumber;
            set
            {
                _serialNumber = value;
                UpdateUserInterface();
            }
        }

        private void UpdateAppearance()
        {
            _appearance.SetData(WiresVisuals.MaintenancePanelState, IsPanelOpen && IsPanelVisible);
        }

        /// <summary>
        /// Contains all registered wires.
        /// </summary>
        public readonly List<Wire> WiresList = new List<Wire>();

        /// <summary>
        /// Status messages are displayed at the bottom of the UI.
        /// </summary>
        private readonly Dictionary<object, object> _statuses = new Dictionary<object, object>();

        /// <summary>
        /// <see cref="AssignAppearance"/> and <see cref="WiresBuilder.CreateWire"/>.
        /// </summary>
        private readonly List<WireColor> _availableColors =
            new List<WireColor>((WireColor[]) Enum.GetValues(typeof(WireColor)));

        private readonly List<WireLetter> _availableLetters =
            new List<WireLetter>((WireLetter[]) Enum.GetValues(typeof(WireLetter)));

        private string _boardName = default!;

        private string? _serialNumber;

        // Used to generate wire appearance randomization client side.
        // We honestly don't care what it is or such but do care that it doesn't change between UI re-opens.
        [ViewVariables]
        private int _wireSeed;
        [ViewVariables]
        private string? _layoutId;

        public override void Initialize()
        {
            base.Initialize();
            _audioSystem = EntitySystem.Get<AudioSystem>();
            _appearance = Owner.GetComponent<AppearanceComponent>();
            _appearance.SetData(WiresVisuals.MaintenancePanelState, IsPanelOpen);
            _userInterface = Owner.GetComponent<ServerUserInterfaceComponent>()
                .GetBoundUserInterface(WiresUiKey.Key);
            _userInterface.OnReceiveMessage += UserInterfaceOnReceiveMessage;
        }

        private void GenerateSerialNumber()
        {
            var random = IoCManager.Resolve<IRobustRandom>();
            Span<char> data = stackalloc char[9];
            data[4] = '-';

            if (random.Prob(0.01f))
            {
                for (var i = 0; i < 4; i++)
                {
                    // Cyrillic Letters
                    data[i] = (char) random.Next(0x0410, 0x0430);
                }
            }
            else
            {
                for (var i = 0; i < 4; i++)
                {
                    // Letters
                    data[i] = (char) random.Next(0x41, 0x5B);
                }
            }

            for (var i = 5; i < 9; i++)
            {
                // Digits
                data[i] = (char) random.Next(0x30, 0x3A);
            }

            SerialNumber = new string(data);
        }

        protected override void Startup()
        {
            base.Startup();


            WireLayout? layout = null;
            var hackingSystem = EntitySystem.Get<WireHackingSystem>();
            if (_layoutId != null)
            {
                hackingSystem.TryGetLayout(_layoutId, out layout);
            }

            foreach (var wiresProvider in Owner.GetAllComponents<IWires>())
            {
                var builder = new WiresBuilder(this, wiresProvider, layout);
                wiresProvider.RegisterWires(builder);
            }

            if (layout != null)
            {
                WiresList.Sort((a, b) =>
                {
                    var pA = layout.Specifications[a.Identifier].Position;
                    var pB = layout.Specifications[b.Identifier].Position;

                    return pA.CompareTo(pB);
                });
            }
            else
            {
                IoCManager.Resolve<IRobustRandom>().Shuffle(WiresList);

                if (_layoutId != null)
                {
                    var dict = new Dictionary<object, WireLayout.WireData>();
                    for (var i = 0; i < WiresList.Count; i++)
                    {
                        var d = WiresList[i];
                        dict.Add(d.Identifier, new WireLayout.WireData(d.Letter, d.Color, i));
                    }

                    hackingSystem.AddLayout(_layoutId, new WireLayout(dict));
                }
            }

            var id = 0;
            foreach (var wire in WiresList)
            {
                wire.Id = ++id;
            }

            UpdateUserInterface();
        }

        /// <summary>
        /// Returns whether the wire associated with <see cref="identifier"/> is cut.
        /// </summary>
        /// <exception cref="ArgumentException"></exception>
        public bool IsWireCut(object identifier)
        {
            var wire = WiresList.Find(x => x.Identifier.Equals(identifier));
            if (wire == null) throw new ArgumentException();
            return wire.IsCut;
        }

        public class Wire
        {
            /// <summary>
            /// The component that registered the wire.
            /// </summary>
            public IWires Owner { get; }

            /// <summary>
            /// Whether the wire is cut.
            /// </summary>
            public bool IsCut { get; set; }

            /// <summary>
            /// Used in client-server communication to identify a wire without telling the client what the wire does.
            /// </summary>
            [ViewVariables]
            public int Id { get; set; }

            /// <summary>
            /// The color of the wire.
            /// </summary>
            [ViewVariables]
            public WireColor Color { get; }

            /// <summary>
            /// The greek letter shown below the wire.
            /// </summary>
            [ViewVariables]
            public WireLetter Letter { get; }

            /// <summary>
            /// Registered by components implementing IWires, used to identify which wire the client interacted with.
            /// </summary>
            [ViewVariables]
            public object Identifier { get; }

            public Wire(IWires owner, bool isCut, WireColor color, WireLetter letter, object identifier)
            {
                Owner = owner;
                IsCut = isCut;
                Color = color;
                Letter = letter;
                Identifier = identifier;
            }
        }

        /// <summary>
        /// Used by <see cref="IWires.RegisterWires"/>.
        /// </summary>
        public class WiresBuilder
        {
            [NotNull] private readonly WiresComponent _wires;
            [NotNull] private readonly IWires _owner;
            private readonly WireLayout? _layout;

            public WiresBuilder(WiresComponent wires, IWires owner, WireLayout? layout)
            {
                _wires = wires;
                _owner = owner;
                _layout = layout;
            }

            public void CreateWire(object identifier, (WireColor, WireLetter)? appearance = null, bool isCut = false)
            {
                WireLetter letter;
                WireColor color;
                if (!appearance.HasValue)
                {
                    if (_layout != null && _layout.Specifications.TryGetValue(identifier, out var specification))
                    {
                        color = specification.Color;
                        letter = specification.Letter;
                        _wires._availableColors.Remove(color);
                        _wires._availableLetters.Remove(letter);
                    }
                    else
                    {
                        (color, letter) = _wires.AssignAppearance();
                    }
                }
                else
                {
                    (color, letter) = appearance.Value;
                    _wires._availableColors.Remove(color);
                    _wires._availableLetters.Remove(letter);
                }

                // TODO: ENSURE NO RANDOM OVERLAP.
                _wires.WiresList.Add(new Wire(_owner, isCut, color, letter, identifier));
            }
        }

        /// <summary>
        /// Picks a color from <see cref="_availableColors"/> and removes it from the list.
        /// </summary>
        /// <returns>The picked color.</returns>
        private (WireColor, WireLetter) AssignAppearance()
        {
            var color = _availableColors.Count == 0 ? WireColor.Red : _random.PickAndTake(_availableColors);
            var letter = _availableLetters.Count == 0 ? WireLetter.α : _random.PickAndTake(_availableLetters);

            return (color, letter);
        }

        /// <summary>
        /// Call this from other components to open the wires UI.
        /// </summary>
        public void OpenInterface(IPlayerSession session)
        {
            _userInterface.Open(session);
        }

        private void UserInterfaceOnReceiveMessage(ServerBoundUserInterfaceMessage serverMsg)
        {
            var message = serverMsg.Message;
            switch (message)
            {
                case WiresActionMessage msg:
                    var wire = WiresList.Find(x => x.Id == msg.Id);
                    var player = serverMsg.Session.AttachedEntity;
                    if (wire == null || player == null)
                    {
                        return;
                    }

                    if (!player.TryGetComponent(out IHandsComponent handsComponent))
                    {
                        _notifyManager.PopupMessage(Owner.Transform.GridPosition, player,
                            Loc.GetString("You have no hands."));
                        return;
                    }

                    if (!EntitySystem.Get<SharedInteractionSystem>().InRangeUnobstructed(player.Transform.MapPosition, Owner.Transform.MapPosition, ignoredEnt: Owner))
                    {
                        _notifyManager.PopupMessage(Owner.Transform.GridPosition, player,
                            Loc.GetString("You can't reach there!"));
                        return;
                    }

                    var activeHandEntity = handsComponent.GetActiveHand?.Owner;
                    ToolComponent? tool = null;
                    activeHandEntity?.TryGetComponent(out tool);

                    switch (msg.Action)
                    {
                        case WiresAction.Cut:
                            if (tool == null || !tool.HasQuality(ToolQuality.Cutting))
                            {
                                _notifyManager.PopupMessageCursor(player,
                                    Loc.GetString("You need to hold a wirecutter in your hand!"));
                                return;
                            }

                            tool.PlayUseSound();
                            wire.IsCut = true;
                            UpdateUserInterface();
                            break;
                        case WiresAction.Mend:
                            if (tool == null || !tool.HasQuality(ToolQuality.Cutting))
                            {
                                _notifyManager.PopupMessageCursor(player,
                                    Loc.GetString("You need to hold a wirecutter in your hand!"));
                                return;
                            }

                            tool.PlayUseSound();
                            wire.IsCut = false;
                            UpdateUserInterface();
                            break;
                        case WiresAction.Pulse:
                            if (tool == null || !tool.HasQuality(ToolQuality.Multitool))
                            {
                                _notifyManager.PopupMessageCursor(player,
                                    Loc.GetString("You need to hold a multitool in your hand!"));
                                return;
                            }

                            if (wire.IsCut)
                            {
                                _notifyManager.PopupMessageCursor(player,
                                    Loc.GetString("You can't pulse a wire that's been cut!"));
                                return;
                            }

                            _audioSystem.PlayFromEntity("/Audio/Effects/multitool_pulse.ogg", Owner);
                            break;
                    }

                    wire.Owner.WiresUpdate(new WiresUpdateEventArgs(wire.Identifier, msg.Action));
                    break;
            }
        }

        private void UpdateUserInterface()
        {
            var clientList = new List<ClientWire>();
            foreach (var entry in WiresList)
            {
                clientList.Add(new ClientWire(entry.Id, entry.IsCut, entry.Color,
                    entry.Letter));
            }

            _userInterface.SetState(
                new WiresBoundUserInterfaceState(
                    clientList.ToArray(),
                    _statuses.Select(p => new StatusEntry(p.Key, p.Value)).ToArray(),
                    BoardName,
                    SerialNumber,
                    _wireSeed));
        }

        public override void ExposeData(ObjectSerializer serializer)
        {
            base.ExposeData(serializer);

            serializer.DataField(ref _boardName, "BoardName", "Wires");
            serializer.DataField(ref _serialNumber, "SerialNumber", null);
            serializer.DataField(ref _wireSeed, "WireSeed", 0);
            serializer.DataField(ref _layoutId, "LayoutId", null);
        }

        bool IInteractUsing.InteractUsing(InteractUsingEventArgs eventArgs)
        {
            if (!eventArgs.Using.TryGetComponent<ToolComponent>(out var tool))
                return false;
            if (!tool.UseTool(eventArgs.User, Owner, ToolQuality.Screwing))
                return false;

            IsPanelOpen = !IsPanelOpen;
            EntitySystem.Get<AudioSystem>()
                .PlayFromEntity(IsPanelOpen ? "/Audio/Machines/screwdriveropen.ogg" : "/Audio/Machines/screwdriverclose.ogg",
                    Owner);
            return true;
        }

        void IExamine.Examine(FormattedMessage message, bool inDetailsRange)
        {
            var loc = IoCManager.Resolve<ILocalizationManager>();

            message.AddMarkup(loc.GetString(IsPanelOpen
                ? "The [color=lightgray]maintenance panel[/color] is [color=darkgreen]open[/color]."
                : "The [color=lightgray]maintenance panel[/color] is [color=darkred]closed[/color]."));
        }

        public void SetStatus(object statusIdentifier, object status)
        {
            if (_statuses.TryGetValue(statusIdentifier, out var storedMessage))
            {
                if (storedMessage == status)
                {
                    return;
                }
            }

            _statuses[statusIdentifier] = status;
            UpdateUserInterface();
        }

        void IMapInit.MapInit()
        {
            if (SerialNumber == null)
            {
                GenerateSerialNumber();
            }

            if (_wireSeed == 0)
            {
                _wireSeed = IoCManager.Resolve<IRobustRandom>().Next(1, int.MaxValue);
                UpdateUserInterface();
            }
        }
    }
}
