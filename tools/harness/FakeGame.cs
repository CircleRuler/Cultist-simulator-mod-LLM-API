using System;
using System.Collections;
using System.Collections.Generic;
using SecretHistories.Entities;
using SecretHistories.Fucine;
using SecretHistories.Services;

// The real compendium lives in the global namespace; cs_gameapi looks it up by that plain name.
public class Compendium
{
    public readonly List<object> Entities = new List<object>();

    /// <summary>Mirrors the game's generic accessor, which cs_gameapi resolves reflectively.</summary>
    public List<T> GetEntitiesAsList<T>()
    {
        var result = new List<T>();
        foreach (object e in Entities)
        {
            if (e is T) result.Add((T)e);
        }
        return result;
    }

    public object GetEntityById(string id)
    {
        foreach (object e in Entities)
        {
            if ((string)e.GetType().GetProperty("Id").GetValue(e, null) == id) return e;
        }
        return null;
    }
}

namespace SecretHistories.Services
{
    /// <summary>Stand-in for the scene loader, recording which game-load path was taken.</summary>
    public class StageHand
    {
        public static int LoadGameOnTabletopCalls;
        public static int NewGameScreenCalls;
        public static object LastSource;
        public static string ForemostSceneName = "Menu";
        public static readonly List<string> ActiveScenes = new List<string> { "Menu" };

        public void LoadGameOnTabletop(object source)
        {
            LoadGameOnTabletopCalls++;
            LastSource = source;
        }

        public void NewGameScreen()
        {
            NewGameScreenCalls++;
        }

        public void MenuScreen()
        {
        }

        /// <summary>UnityEngine.SceneManagement.Scene only carries a name; the fake exposes it plainly.</summary>
        public object GetForemostScene()
        {
            return new FakeScene { name = ForemostSceneName };
        }

        public bool SceneIsActive(string sceneToCheck)
        {
            return ActiveScenes.Contains(sceneToCheck);
        }
    }

    public class FakeScene
    {
        public string name;
    }

    public class FreshGameProvider
    {
        public object Legacy;

        public FreshGameProvider(object legacy)
        {
            Legacy = legacy;
        }
    }
}

namespace SecretHistories.Infrastructure.Persistence
{
    public class DefaultGamePersistenceProvider
    {
        public static int Constructed;

        public DefaultGamePersistenceProvider()
        {
            Constructed++;
        }
    }
}

namespace SecretHistories.Infrastructure
{
    /// <summary>
    /// Stand-in for the menu screen controller. cs_gameapi calls the same three methods the menu
    /// buttons call, so the fake records them the way the real one would act on them.
    /// </summary>
    public class MenuScreenController
    {
        public static int BeginDefaultCalls;
        public static int BeginSpecifiedCalls;
        public static int ContinueCalls;
        public static string LastLegacyId;
        public static bool RefuseInput;

        /// <summary>The real menu exposes this and every handler early-returns when it is false.</summary>
        public bool canTakeInput = true;

        public void BeginGameWithDefaultLegacy()
        {
            if (RefuseInput || !canTakeInput) return;
            BeginDefaultCalls++;
        }

        public void BeginNewSaveWithSpecifiedLegacy(string legacyId)
        {
            if (RefuseInput || !canTakeInput) return;
            BeginSpecifiedCalls++;
            LastLegacyId = legacyId;
        }

        public void ContinueGame()
        {
            if (RefuseInput || !canTakeInput) return;
            ContinueCalls++;
        }
    }
}

// A stand-in for the parts of Cultist Simulator that cs_gameapi reflects over.
//
// Compiles into its own assembly so that the mod's Reflect.FindType() resolves these types
// exactly the way it resolves the game's, while nothing else takes a compile-time dependency
// on them. Type names mirror the real game's namespaces and members.

namespace SecretHistories.Fucine
{
    public class FucinePath
    {
        public string Path { get; private set; }

        public FucinePath(string path)
        {
            Path = path;
        }

        public override string ToString()
        {
            return Path;
        }

        public override bool Equals(object obj)
        {
            var other = obj as FucinePath;
            return other != null && other.Path == Path;
        }

        public override int GetHashCode()
        {
            return Path == null ? 0 : Path.GetHashCode();
        }
    }
}

namespace SecretHistories.Entities
{
    /// <summary>
    /// Mirrors the game's Legacy entity. cs_gameapi resolves this full name and asks the
    /// compendium for GetEntitiesAsList&lt;Legacy&gt;(), so the fake must match that identity.
    /// </summary>
    public class Legacy
    {
        public string Id;
        public string Label;
    }

    public class FucineRoot
    {
        private static FucineRoot _instance;

        public static void Set(FucineRoot root)
        {
            _instance = root;
        }

        public static FucineRoot Get()
        {
            return _instance;
        }

        public static void Reset()
        {
            _instance = null;
        }
    }

    public class HornedAxe
    {
        private readonly Dictionary<string, object> _spheres = new Dictionary<string, object>();

        public void Register(object sphere)
        {
            // Read the path reflectively: the real HornedAxe works off its own Sphere type, so
            // the fake stays decoupled from the fake sphere implementation.
            var field = sphere.GetType().GetField("AbsolutePath");
            _spheres[(string)field.GetValue(sphere)] = sphere;
        }

        public object GetSphereByAbsolutePath(object path)
        {
            object found;
            if (path != null && _spheres.TryGetValue(path.ToString(), out found)) return found;

            // Mirror the real catalogue: an unknown path does not come back as null, it falls
            // back to a default sphere. cs_gameapi must notice the mismatch and report 404
            // rather than silently operating on the wrong sphere.
            object fallback;
            if (_spheres.TryGetValue(FallbackPath, out fallback)) return fallback;
            return null;
        }

        /// <summary>Path of the sphere returned for unrecognised paths, mimicking the game.</summary>
        public string FallbackPath = "~/tabletop";

        public List<object> GetSpheres()
        {
            return new List<object>(_spheres.Values);
        }
    }
}

namespace SecretHistories.Entities
{
    public class Context
    {
        public enum ActionSource
        {
            Unknown = 0,
            PlayerDrag = 2,
            Metafictional = 4,
            Debug = 10,
            UI = 11
        }

        public ActionSource Source;

        public Context(ActionSource source)
        {
            Source = source;
        }

        public override string ToString()
        {
            return "Context(" + Source + ")";
        }
    }
}

namespace SecretHistories.UI
{
    /// <summary>
    /// Stand-in for the game's service locator. cs_gameapi reaches the sphere catalogue through
    /// Watchman.Get&lt;HornedAxe&gt;(), so the fake has to offer the same generic entry point.
    /// </summary>
    public static class Watchman
    {
        private static readonly Dictionary<Type, object> Registered = new Dictionary<Type, object>();

        public static void Register<T>(T toRegister)
        {
            Registered[typeof(T)] = toRegister;
        }

        public static T Get<T>()
        {
            if (SimulateMenuControllerAbsent && typeof(T) == typeof(SecretHistories.Infrastructure.MenuScreenController))
                return default(T);

            object found;
            if (Registered.TryGetValue(typeof(T), out found)) return (T)found;
            return default(T);
        }

        /// <summary>Test hook: makes the menu controller invisible the way an unloaded scene does.</summary>
        public static bool SimulateMenuControllerAbsent;

        public static bool Exists<T>()
        {
            return Registered.ContainsKey(typeof(T));
        }

        public static void ForgetEverything()
        {
            Registered.Clear();
        }
    }
}

namespace cs_gameapi_fake
{
    public class TokenItinerary
    {
        public int DepartCalls;
        public object Destination;

        public void Depart(object token, object context)
        {
            DepartCalls++;
            var t = (FakeToken)token;

            // A real itinerary takes ownership of the token: it detaches it from wherever it
            // was and hands it to the destination. Mirror that, otherwise the origin sphere
            // keeps a stale reference.
            if (t.Sphere != null) t.Sphere.Tokens.Remove(t);

            var destination = (FakeSphere)Destination;
            if (!destination.Tokens.Contains(t)) destination.Tokens.Add(t);
            t.Sphere = destination;
            t.Location = new FakeTokenLocation(new UnityEngine.Vector3(0f, 0f, 0f), destination.AbsolutePath);
        }

        public object GetItineraryFor(object token)
        {
            return this;
        }
    }

    public class FakeSphere
    {
        public string AbsolutePath;
        public string Id;
        public readonly List<object> Tokens = new List<object>();
        public readonly List<FakeDominion> Dominions = new List<FakeDominion>();
        public FakeSpec GoverningSphereSpec;
        public bool RejectEverything;
        public bool Shrouded;
        public bool Defunct;

        public string SphereTypeName = "TabletopSphere";
        public object SphereCategory = "Tabletop";
        public bool AllowDrag = true;
        public bool ContentsHidden;

        /// <summary>When true, GetTokenById() reports a NullToken for ids this sphere lacks.</summary>
        public bool ReturnNullTokenWhenMissing;

        /// <summary>Set when Depart() carries a token here, so tests can see the animated route was used.</summary>
        public bool ArrivedByItinerary;

        public string GetAbsolutePath()
        {
            return new SecretHistories.Fucine.FucinePath(AbsolutePath).ToString();
        }

        public string GetDeckSpecId()
        {
            return "deck_" + Id;
        }

        public List<object> GetElementTokens()
        {
            return Tokens;
        }

        public int GetTotalStacksCount()
        {
            return Tokens.Count;
        }

        public bool IsEmpty()
        {
            return Tokens.Count == 0;
        }

        /// <summary>
        /// Mirrors a real trap in the game: Sphere.GetTokenById() returns a NullToken rather
        /// than null when it cannot find the token. cs_gameapi's FindToken must not be fooled
        /// by it, so the fake reproduces the behaviour.
        /// </summary>
        public object GetTokenById(string id)
        {
            foreach (object t in Tokens)
            {
                if ((string)t.GetType().GetProperty("PayloadId").GetValue(t, null) == id) return t;
            }
            return ReturnNullTokenWhenMissing ? (object)new NullToken() : null;
        }

        public class NullToken
        {
            public string PayloadId = null;
            public string PayloadTypeName = "NullElementStack";
            public object Payload = new NullElementStack();

            public bool MatchesPathTokenId(string other)
            {
                return false;
            }
        }

        public bool TryAcceptToken(object token, object context)
        {
            if (RejectEverything) return false;
            var t = (FakeToken)token;

            // Mirror the real Sphere: remove from the old sphere, adopt, position, notify.
            if (t.Sphere != null) t.Sphere.Tokens.Remove(t);
            Tokens.Add(t);
            t.Sphere = this;
            t.Location = new FakeTokenLocation(new UnityEngine.Vector3(0f, 0f, 0f), AbsolutePath);
            return true;
        }

        public object GetItineraryFor(object token)
        {
            return new TokenItinerary { Destination = this };
        }

        public object GetItineraryFor(object token, UnityEngine.Vector3 desiredPosition)
        {
            return new TokenItinerary { Destination = this };
        }
    }

    /// <summary>
    /// Stand-in for the game's NullElementStack payload, returned by NullToken. Its type name
    /// starting with "Null" is the signal cs_gameapi uses to reject it.
    /// </summary>
    public class NullElementStack : FakePayload
    {
        public NullElementStack()
        {
            Id = "nullelementstack";
            EntityId = "nullelementstack";
        }
    }

    // Named and namespaced like the real entity: cs_gameapi resolves
    // "SecretHistories.Entities.Legacy" and asks the compendium for GetEntitiesAsList<Legacy>().
    public class FakeLegacy : Legacy
    {
    }

    public class FakeSpec
    {
        public string Id;
        public string Label;
        public string Description;
        public bool Greedy;
        public bool Consumes;
        public Dictionary<string, int> Required = new Dictionary<string, int>();
        public Dictionary<string, int> Forbidden = new Dictionary<string, int>();
    }

    public class FakeDominion
    {
        public string Identifier;
        public readonly List<object> Spheres = new List<object>();
    }

    public class FakeTokenLocation
    {
        public UnityEngine.Vector3 LocalPosition;
        public string AtSpherePath;

        public FakeTokenLocation(UnityEngine.Vector3 position, string spherePath)
        {
            LocalPosition = position;
            AtSpherePath = spherePath;
        }
    }

    public class FakeToken
    {
        public string PayloadId;
        public string PayloadEntityId;
        public string PayloadTypeName;
        public object Payload;
        public FakeSphere Sphere;
        public FakeTokenLocation Location = new FakeTokenLocation(new UnityEngine.Vector3(0f, 0f, 0f), "~/tabletop");
        public FakeTokenLocation HomeLocation;
        public bool Defunct;
        public object CurrentState = "DroppedInSphereState";
        public bool CanBeDragged = true;
        public bool CanBePulled = true;
        public int SelectCalls;

        public void Select()
        {
            SelectCalls++;
        }

        public void Deselect()
        {
        }
    }

    public class FakePayload
    {
        public string Id;
        public string EntityId;
        public int Quantity = 1;
        public string Label;
        public string Description;
        public bool Unique;
        public string UniquenessGroup;
        public bool Defunct;
        public float LifetimeRemaining;
        public Dictionary<string, int> Mutations = new Dictionary<string, int>();
        public Dictionary<string, string> Illuminations = new Dictionary<string, string>();
        public readonly List<FakeDominion> Dominions = new List<FakeDominion>();
        public Dictionary<string, int> Aspects = new Dictionary<string, int>();

        public Dictionary<string, int> GetAspects(bool includeSelf)
        {
            return Aspects;
        }
    }

    public class ElementStack : FakePayload
    {
        public bool Decays;
        public FakeElement Element;
    }

    public class FakeElement
    {
        public string Id;
    }

    public class Situation : FakePayload
    {
        public string VerbId;
        public string CurrentRecipeId = "nullrecipe";
        public string RecipeId = "nullrecipe";
        public float TimeRemaining;
        public float Warmup = 60f;
        public bool IsOpen;
        public string State = "UnstartedState";
        public object StateIdentifier = "Unstarted";
        public int TryStartCalls;
        public int OpenCalls;
        public int CloseCalls;

        public void TryStart()
        {
            TryStartCalls++;
            // The real game moves through its state machine, which updates both the state
            // object and the identifier cs_gameapi reads.
            State = "OngoingState";
            StateIdentifier = "Ongoing";
        }

        public void OpenAt(object location)
        {
            OpenCalls++;
            IsOpen = true;
        }

        public void Close()
        {
            CloseCalls++;
            IsOpen = false;
        }
    }

    /// <summary>Builds a board with the same shape as the sample save.json shipped alongside the game.</summary>
    public static class SampleBoard
    {
        public class Board
        {
            public HornedAxe Axe;
            public FucineRoot Root;

            public FakeSphere SphereAt(string path)
            {
                return (FakeSphere)Axe.GetSphereByAbsolutePath(new SecretHistories.Fucine.FucinePath(path));
            }

            public Situation Situation(string tokenId)
            {
                foreach (object s in Axe.GetSpheres())
                {
                    foreach (object t in ((FakeSphere)s).Tokens)
                    {
                        var ft = (FakeToken)t;
                        if (ft.PayloadId == tokenId) return ft.Payload as Situation;
                    }
                }
                return null;
            }
        }

        /// <summary>
        /// A root that exists but holds no tabletop game - the menu and credits screens look
        /// like this, which is why "the root exists" is not a usable in-game test.
        /// </summary>
        public static Board BuildExhibitOnlyBoard()
        {
            var board = new Board();
            var axe = new HornedAxe();
            var root = new FucineRoot();
            board.Axe = axe;

            var limbo = new FakeSphere { AbsolutePath = "~/limbo", Id = "limbo", SphereTypeName = "Limbo" };
            var exhibit = new FakeSphere { AbsolutePath = "~/cardsexhibit", Id = "cardsexhibit", SphereTypeName = "ExhibitCardsSphere" };
            var card = new FakeToken
            {
                PayloadId = "!credits.art_4",
                PayloadTypeName = "ElementStack",
                Payload = new ElementStack { Id = "!credits.art_4", EntityId = "credits.art", Quantity = 1, Label = "美术" },
                Location = new FakeTokenLocation(new UnityEngine.Vector3(0f, 0f, 0f), "~/cardsexhibit")
            };
            exhibit.Tokens.Add(card);
            card.Sphere = exhibit;

            axe.Register(limbo);
            axe.Register(exhibit);
            FucineRoot.Set(root);
            SecretHistories.UI.Watchman.Register(axe);
            board.Root = root;
            return board;
        }

        public static Board Build()
        {
            var board = new Board();
            var axe = new HornedAxe();
            var root = new FucineRoot();
            board.Axe = axe;

            // ~/tabletop with two cards.
            var tabletop = new FakeSphere
            {
                AbsolutePath = "~/tabletop",
                Id = "tabletop",
                GoverningSphereSpec = new FakeSpec { Id = "tabletop", Label = "" }
            };

            var funds = new ElementStack
            {
                Id = "!funds_1",
                EntityId = "funds",
                Quantity = 1,
                Label = "资金",
                Description = "钱。",
                Aspects = new Dictionary<string, int> { { "funds", 2 }, { "ingredient", 1 } }
            };
            var fundsToken = new FakeToken
            {
                PayloadId = "!funds_1",
                PayloadEntityId = "funds",
                PayloadTypeName = "ElementStack",
                Payload = funds,
                Location = new FakeTokenLocation(new UnityEngine.Vector3(-150f, 230f, 0f), "~/tabletop"),
                CanBeDragged = true
            };
            tabletop.Tokens.Add(fundsToken);
            fundsToken.Sphere = tabletop;

            var introjob = new ElementStack
            {
                Id = "!introjob_2",
                EntityId = "introjob",
                Quantity = 1,
                Label = "一份工作邀请",
                Aspects = new Dictionary<string, int> { { "job", 2 } }
            };
            var introjobToken = new FakeToken
            {
                PayloadId = "!introjob_2",
                PayloadEntityId = "introjob",
                PayloadTypeName = "ElementStack",
                Payload = introjob,
                Location = new FakeTokenLocation(new UnityEngine.Vector3(-300f, 130f, 0f), "~/tabletop")
            };
            tabletop.Tokens.Add(introjobToken);
            introjobToken.Sphere = tabletop;

            // A verb: ~/tabletop!work_0 with a work slot and a notes sphere.
            var workSlot = new FakeSphere
            {
                AbsolutePath = "~/tabletop!work_0/work",
                Id = "work",
                GoverningSphereSpec = new FakeSpec
                {
                    Id = "work",
                    Label = "作业",
                    Required = new Dictionary<string, int> { { "job", 1 } }
                }
            };
            var lockedSlot = new FakeSphere
            {
                AbsolutePath = "~/tabletop!work_0/locked",
                Id = "locked",
                RejectEverything = true,
                GoverningSphereSpec = new FakeSpec { Id = "locked" }
            };
            var notesSphere = new FakeSphere
            {
                AbsolutePath = "~/tabletop!work_0/notessphere",
                Id = "notessphere",
                SphereTypeName = "NotesSphere",
                GoverningSphereSpec = new FakeSpec { Id = "notessphere" }
            };

            var situation = new Situation
            {
                Id = "!work_0",
                EntityId = "Situation",
                VerbId = "work",
                Label = "作业",
                CurrentRecipeId = "nullrecipe",
                TimeRemaining = 0f,
                Warmup = 60f,
                State = "Unstarted"
            };

            var thresholds = new FakeDominion { Identifier = "VerbThresholds" };
            thresholds.Spheres.Add(workSlot);
            thresholds.Spheres.Add(lockedSlot);
            situation.Dominions.Add(thresholds);
            var notes = new FakeDominion { Identifier = "Notes" };
            notes.Spheres.Add(notesSphere);
            situation.Dominions.Add(notes);

            var workToken = new FakeToken
            {
                PayloadId = "!work_0",
                PayloadEntityId = "Situation",
                PayloadTypeName = "Situation",
                Payload = situation,
                Location = new FakeTokenLocation(new UnityEngine.Vector3(0f, 0f, 0f), "~/tabletop"),
                CanBeDragged = false
            };
            tabletop.Tokens.Add(workToken);
            workToken.Sphere = tabletop;

            // A sphere whose GetTokenById() reports a NullToken for anything it does not hold.
            // Registered after the tabletop so FindToken has to survive the trap on its way to
            // the sphere that really owns the token.
            var trap = new FakeSphere
            {
                AbsolutePath = "~!mansus/woodtrap",
                Id = "woodtrap",
                SphereTypeName = "OutputSphere",
                ReturnNullTokenWhenMissing = true,
                GoverningSphereSpec = new FakeSpec { Id = "woodtrap" }
            };

            // Plumbing spheres present in the real game's sphere list.
            var windows = new FakeSphere { AbsolutePath = "~/windows", Id = "windows" };
            var enroute = new FakeSphere { AbsolutePath = "~/enroute", Id = "enroute" };

            foreach (FakeSphere s in new[] { trap, windows, enroute, workSlot, lockedSlot, notesSphere })
            {
                axe.Register(s);
            }

            // A card reachable only after passing the trap sphere: it lives on its own sphere so
            // it does not disturb the tabletop counts the other tests assert on.
            var two = new ElementStack
            {
                Id = "!not_a_two",
                EntityId = "reason",
                Quantity = 2,
                Label = "理智",
                Aspects = new Dictionary<string, int> { { "reason", 2 } }
            };
            var twoToken = new FakeToken
            {
                PayloadId = "!not_a_two",
                PayloadEntityId = "reason",
                PayloadTypeName = "ElementStack",
                Payload = two,
                Location = new FakeTokenLocation(new UnityEngine.Vector3(10f, 20f, 0f), "~!mansus/woodtrap/reasonstack")
            };
            var reasonStack = new FakeSphere
            {
                AbsolutePath = "~!mansus/woodtrap/reasonstack",
                Id = "reasonstack",
                SphereTypeName = "OutputSphere",
                GoverningSphereSpec = new FakeSpec { Id = "reasonstack" }
            };
            reasonStack.Tokens.Add(twoToken);
            twoToken.Sphere = reasonStack;

            axe.Register(tabletop);
            axe.Register(reasonStack);

            FucineRoot.Set(root);
            SecretHistories.UI.Watchman.Register(axe);

            // Content + menu plumbing, so the game-start endpoints have something to talk to.
            var compendium = new Compendium();
            compendium.Entities.Add(new FakeLegacy { Id = "aspirant", Label = "有志青年" });
            compendium.Entities.Add(new FakeLegacy { Id = "adept", Label = "入门者" });
            SecretHistories.UI.Watchman.Register(compendium);
            SecretHistories.UI.Watchman.Register(new StageHand());
            SecretHistories.UI.Watchman.Register(new SecretHistories.Infrastructure.MenuScreenController());

            board.Root = root;
            return board;
        }
    }
}
