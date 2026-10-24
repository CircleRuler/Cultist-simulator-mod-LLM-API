using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace CsGameApi
{
    /// <summary>
    /// The four mutating operations the external API exposes, implemented against the game's own
    /// methods so that angels, dominions, recipe triggers and chronicling all behave exactly as
    /// they do for a real player drag.
    ///
    /// All of these run on the Unity main thread; the HTTP layer never calls them directly.
    /// </summary>
    internal static class GameActions
    {
        // --- place: put a card into a card slot -------------------------------------------

        /// <summary>
        /// Move a token into a sphere (a "card slot" when the sphere is a situation's threshold
        /// or interior sphere). Uses Sphere.TryAcceptToken, which is what a player drop runs.
        /// </summary>
        public static Dictionary<string, object> Place(string tokenId, string spherePath)
        {
            var result = NewResult("place");
            if (string.IsNullOrEmpty(tokenId)) throw new ApiException(400, "'token' is required");
            if (string.IsNullOrEmpty(spherePath)) throw new ApiException(400, "'slot' (sphere path) is required");

            object destination = Reflect.SphereByPath(spherePath);
            object token = FindToken(tokenId);
            if (token == null) throw new ApiException(404, "no token with id '" + tokenId + "'");

            object context = Reflect.MakeContext("Debug");
            result["token"] = tokenId;
            result["slot"] = spherePath;

            // A route's OutputSphere is where the game PUTS the card once the route completes -
            // it is not an entrance. Accepting there skips the recipe and EvictToken chain, so
            // whatever represents "entering" (a mansus Ingress) never completes. Redirect to the
            // gate that feeds it, which is what a player dropping onto the map icon does.
            object entry = ResolveEntrySphere(destination, spherePath, result);
            if (entry != null) destination = entry;

            result["slotSpecId"] = Reflect.GetString(Reflect.Get(destination, "GoverningSphereSpec"), "Id");

            bool accepted = InvokeTryAccept(destination, token, context);
            result["accepted"] = accepted;
            result["movedTo"] = Reflect.SpherePath(Reflect.Get(token, "Sphere"));

            if (!accepted)
            {
                result["success"] = false;
                result["reason"] = "sphere.TryAcceptToken returned false; the slot rejected the card (check its required/forbidden aspects)";
                return result;
            }

            // A stranded Ingress blocks the mansus overlay and cannot be cleared by hand, so
            // sweep for one whenever we put something on the board.
            List<object> swept = RetireOrphanIngress();
            if (swept.Count > 0) result["retiredOrphanIngress"] = swept;

            result["success"] = true;
            return result;
        }

        /// <summary>
        /// If the requested sphere is an OutputSphere, swap it for the EgressThreshold that leads
        /// into it and record the substitution. Returns null when no substitution applies.
        ///
        /// An OutputSphere with no gate at all is a genuine dead end, so that case is reported
        /// rather than silently accepted.
        /// </summary>
        private static object ResolveEntrySphere(object destination, string requestedPath, Dictionary<string, object> result)
        {
            if (!Reflect.IsOutputSphere(destination)) return null;

            object gate = Reflect.FindEgressThresholdFor(destination);
            if (gate != null)
            {
                result["redirectedFrom"] = requestedPath;
                result["redirectedTo"] = Reflect.SpherePath(gate);
                result["redirectedWhy"] = "OutputSphere is the route's destination; placed into the EgressThreshold that feeds it instead";
                result["slot"] = Reflect.SpherePath(gate);
                return gate;
            }

            // No gate declares this sphere as its eviction destination (the mansus does not set
            // that field the way EgressThreshold.EvictToken implies it might). Rather than claim
            // no entrance exists, name the entrances that ARE there so the caller can pick one -
            // the round frame on the map is what a card has to be dropped into.
            var gates = new List<string>();
            try
            {
                foreach (object s in Reflect.AllSpheres())
                {
                    if (s == null) continue;
                    if (!string.Equals(s.GetType().Name, "EgressThreshold", StringComparison.Ordinal)) continue;
                    string p = Reflect.SpherePath(s);
                    if (string.IsNullOrEmpty(p)) continue;
                    gates.Add(p);
                }
            }
            catch { }

            string hint = gates.Count > 0
                ? " Available entrances: " + string.Join(", ", gates.ToArray()) + "."
                : " No EgressThreshold is registered right now.";

            throw new ApiException(409,
                "'" + requestedPath + "' is an OutputSphere - a route's destination, not an entrance. "
                + "A card dropped there never runs the route's recipe."
                + hint);
        }

        private static bool InvokeTryAccept(object sphere, object token, object context)
        {
            MethodInfo m = Reflect.Method(sphere.GetType(), "TryAcceptToken", 2);
            if (m == null) throw new ApiException(500, "Sphere.TryAcceptToken not found");
            try
            {
                object r = m.Invoke(sphere, new[] { token, context });
                return r is bool && (bool)r;
            }
            catch (Exception e)
            {
                throw new ApiException(500, "TryAcceptToken threw: " + Reflect.Unwrap(e).Message);
            }
        }

        // --- closeworld: dismiss the mansus (otherworld) overlay ----------------------------

        /// <summary>
        /// Dismiss every open otherworld overlay - the mansus map and friends.
        ///
        /// This is what the report's problem 2 actually needs. Retiring the stranded Ingress is
        /// not enough: Numa.OpenIngress retires the ingress as part of ENTERING, and it is
        /// Otherworld.Hide() that takes the map down again. Otherworld.Close() calls Hide().
        ///
        /// Hide() begins with "if (!_transitionFx.CanHide) return;" - so it can decline while a
        /// transition is playing. That is why this reports the overlay's IsOpen afterwards rather
        /// than assuming the call worked.
        /// </summary>
        public static Dictionary<string, object> CloseWorld()
        {
            var result = NewResult("closeworld");
            Type otherworld = Reflect.FindType("SecretHistories.Otherworlds.Otherworld");
            if (otherworld == null) throw new ApiException(500, "Otherworld type not found in this game build");

            List<object> worlds = Reflect.AllComponentsInScene(otherworld);
            result["found"] = worlds.Count;

            var closed = new List<object>();
            var stillOpen = new List<object>();
            var details = new List<object>();

            foreach (object w in worlds)
            {
                string id = Reflect.GetString(w, "Id");
                bool wasOpen = Reflect.GetBool(w, "IsOpen");

                MethodInfo close = Reflect.Method(w.GetType(), "Close", 0);
                if (close != null)
                {
                    try { close.Invoke(w, null); } catch { }
                }

                // Hide() may decline while a transition is playing; give it a moment before judging.
                bool nowOpen = Reflect.GetBool(w, "IsOpen");
                if (wasOpen && nowOpen)
                {
                    System.Threading.Thread.Sleep(150);
                    try { if (close != null) close.Invoke(w, null); } catch { }
                    nowOpen = Reflect.GetBool(w, "IsOpen");
                }

                details.Add(new Dictionary<string, object>
                {
                    { "id", id },
                    { "wasOpen", wasOpen },
                    { "isOpen", nowOpen }
                });

                if (wasOpen && !nowOpen) closed.Add(id);
                else if (nowOpen) stillOpen.Add(id);
            }

            result["otherworlds"] = details;
            result["closed"] = closed;
            result["stillOpen"] = stillOpen;
            result["success"] = stillOpen.Count == 0;
            if (stillOpen.Count > 0)
                result["reason"] = "the overlay refused to hide (Otherworld.Hide returns early unless _transitionFx.CanHide); "
                                 + "wait for the transition to finish and retry";
            else if (closed.Count == 0)
                result["note"] = "no otherworld overlay was open";
            return result;
        }

        // --- tidy: merge same-card stacks on the tabletop ----------------------------------

        /// <summary>
        /// Merge every stack of identical cards on the tabletop into one - the game's "tidy"
        /// button. TabletopChoreographer.GroupAllStacks groups the sphere's element tokens by
        /// payload and merges each group.
        ///
        /// Deliberately NOT just "call collect": collect concludes open situations as a side
        /// effect (SituationCollectAll), which cancels a verb that is still running - so using it
        /// to tidy would quietly kill the player's in-progress work. Tidy only re-stacks what is
        /// already lying on the table.
        /// </summary>
        public static Dictionary<string, object> Tidy()
        {
            var result = NewResult("tidy");
            object tabletop = Reflect.SphereByPath("~/tabletop");

            result["before"] = TabletopStacks(tabletop);

            bool ok = Reflect.GroupAllStacks(tabletop);
            result["grouped"] = ok;

            // The merge is animated, so give it a moment before reporting the outcome.
            System.Threading.Thread.Sleep(250);
            result["after"] = TabletopStacks(tabletop);

            result["success"] = ok;
            if (!ok)
                result["reason"] = "this sphere has no choreographer with GroupAllStacks in this game build";
            return result;
        }

        /// <summary>How many card stacks sit on a sphere, and how many distinct cards they cover.</summary>
        private static Dictionary<string, object> TabletopStacks(object sphere)
        {
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            int stacks = 0;
            int totalQuantity = 0;

            try
            {
                foreach (object t in Reflect.ToList(Reflect.Get(sphere, "Tokens")))
                {
                    if (t == null) continue;
                    object payload = Reflect.Get(t, "Payload");
                    if (payload == null) continue;
                    if (!string.Equals(payload.GetType().Name, "ElementStack", StringComparison.Ordinal)) continue;

                    // Group by card TYPE (PayloadEntityId), not PayloadId: the id is unique per
                    // stack, so grouping by it reported "24 distinct cards" when there were 16
                    // and made the before/after comparison meaningless.
                    string type = Reflect.GetString(t, "PayloadEntityId");
                    if (type == null) type = Reflect.GetString(t, "PayloadId");
                    if (type == null) continue;

                    stacks++;
                    totalQuantity += Reflect.GetInt(t, "Quantity", 1);
                    counts[type] = (counts.TryGetValue(type, out int prev) ? prev : 0) + 1;
                }
            }
            catch { }

            // Stacks that are still split after a tidy are the interesting ones.
            var split = new List<object>();
            foreach (var kv in counts)
            {
                if (kv.Value < 2) continue;
                split.Add(new Dictionary<string, object> { { "card", kv.Key }, { "stacks", kv.Value } });
                if (split.Count >= 8) break;
            }

            return new Dictionary<string, object>
            {
                { "stacks", stacks },
                { "distinctCards", counts.Count },
                { "totalQuantity", totalQuantity },
                { "stillSplit", split }
            };
        }

        // --- retire: rescue a stranded token ------------------------------------------------

        /// <summary>
        /// Retire a token - Token.Retire(). This is the escape hatch for cards the API can no
        /// longer act on: a mansus Ingress left stranded on the tabletop by an earlier build stays
        /// draggable:false / interactable:false forever, keeps the map overlay on screen, and no
        /// other endpoint can shift it.
        ///
        /// Prefer fixing the cause (placing into the EgressThreshold instead of the OutputSphere),
        /// which ResolveEntrySphere now does; this exists for saves already damaged that way.
        /// </summary>
        public static Dictionary<string, object> Retire(string tokenId)
        {
            var result = NewResult("retire");
            if (string.IsNullOrEmpty(tokenId)) throw new ApiException(400, "'token' is required");

            object token = FindToken(tokenId);
            if (token == null) throw new ApiException(404, "no token with id '" + tokenId + "'");

            object payload = Reflect.Get(token, "Payload");
            string payloadType = payload == null ? null : payload.GetType().Name;

            result["token"] = tokenId;
            result["payloadType"] = payloadType;
            result["sphereBefore"] = Reflect.SpherePath(Reflect.Get(token, "Sphere"));

            MethodInfo retire = Reflect.Method(token.GetType(), "Retire", 0);
            if (retire == null) throw new ApiException(500, "Token.Retire() not found in this game build");
            try
            {
                object r = retire.Invoke(token, null);
                result["retired"] = r is bool ? r : (object)true;
            }
            catch (Exception e)
            {
                throw new ApiException(500, "Token.Retire threw: " + Reflect.Unwrap(e).Message);
            }

            result["stillPresent"] = FindToken(tokenId) != null;
            result["success"] = true;
            return result;
        }

        /// <summary>
        /// Retire Ingress tokens stranded on the tabletop, and report them.
        ///
        /// An Ingress represents "someone is entering the mansus". While it is live it belongs to
        /// the map, not to the playfield, and the game retires it as soon as the route completes.
        /// One sitting on ~/tabletop is therefore an orphan: it never completes, so the map overlay
        /// is never dismissed, and it cannot be dragged or interacted with to clear it by hand.
        /// </summary>
        private static List<object> RetireOrphanIngress()
        {
            var retired = new List<object>();
            var victims = new List<object>();

            foreach (var kv in AllTokens())
            {
                object payload = Reflect.Get(kv.Value, "Payload");
                if (payload == null) continue;
                if (!string.Equals(payload.GetType().Name, "Ingress", StringComparison.Ordinal)) continue;

                string spherePath = Reflect.SpherePath(Reflect.Get(kv.Value, "Sphere"));
                if (!string.Equals(spherePath, "~/tabletop", StringComparison.Ordinal)) continue;
                victims.Add(kv.Value);
            }

            foreach (object t in victims)
            {
                string id = Reflect.GetString(t, "PayloadId");
                MethodInfo retire = Reflect.Method(t.GetType(), "Retire", 0);
                if (retire == null) continue;
                try
                {
                    retire.Invoke(t, null);
                    retired.Add(id);
                }
                catch { }
            }
            return retired;
        }

        // --- start: click the verb button --------------------------------------------------

        /// <summary>
        /// Start a verb/situation, i.e. press its "begin" button.
        ///
        /// Normally this is Situation.TryStart(), which picks a recipe for you. That choice can
        /// land on a hint-only recipe (Recipe.Craftable == false, HintOnly == true) and TryStart
        /// then returns without doing anything - the game's CanExecuteInContext does not test
        /// Craftable, so a loose hint recipe outranks the real one. Illness is the clearest case:
        /// "tryrecoverillhealth" (needs illhealth only) beats "recoverillhealthfunds" (needs
        /// illhealth + funds), so a sick character could never be treated through the API.
        ///
        /// Two ways out, both here:
        ///   * pass "recipe" to name the exact recipe to run - Situation.StartRecipe(recipe);
        ///   * or pass nothing and let this skip an unstartable prediction in favour of the first
        ///     craftable recipe that the current contents actually satisfy.
        /// </summary>
        public static Dictionary<string, object> Start(string situationTokenId, string recipeId = null)
        {
            var result = NewResult("start");
            if (string.IsNullOrEmpty(situationTokenId)) throw new ApiException(400, "'token' is required");

            object token = FindToken(situationTokenId);
            if (token == null) throw new ApiException(404, "no token with id '" + situationTokenId + "'");

            object payload = Reflect.Get(token, "Payload");
            if (payload == null) throw new ApiException(409, "token '" + situationTokenId + "' has no payload");

            string payloadType = payload.GetType().Name;
            if (payloadType != "Situation")
                throw new ApiException(409, "token '" + situationTokenId + "' is a " + payloadType + ", not a Situation; only verbs can be started");

            result["token"] = situationTokenId;
            result["verbId"] = Reflect.GetString(payload, "VerbId");
            result["stateBefore"] = StateOf(payload);
            result["recipeBefore"] = Reflect.GetString(payload, "CurrentRecipeId");

            // An explicitly named recipe always wins: the caller knows better than the picker.
            if (!string.IsNullOrEmpty(recipeId))
            {
                object recipe = Reflect.FindRecipeById(recipeId);
                if (recipe == null) throw new ApiException(404, "no recipe with id '" + recipeId + "'");

                MethodInfo startRecipe = Reflect.Method(payload.GetType(), "StartRecipe", 1);
                if (startRecipe == null) throw new ApiException(500, "Situation.StartRecipe not found");
                try { startRecipe.Invoke(payload, new[] { recipe }); }
                catch (Exception e) { throw new ApiException(500, "StartRecipe threw: " + Reflect.Unwrap(e).Message); }

                result["recipeForced"] = recipeId;
                result["recipeCraftable"] = Reflect.GetBool(recipe, "Craftable");
                return FinishStart(result, payload);
            }

            // Let the game pick, then check whether the pick is actually runnable.
            MethodInfo predict = Reflect.Method(payload.GetType(), "UpdateRecipePrediction", 0);
            if (predict != null)
            {
                try { predict.Invoke(payload, null); } catch { }
            }

            object predicted = Reflect.Get(payload, "CurrentRecipe");
            string predictedId = Reflect.GetString(payload, "CurrentRecipeId");
            bool predictedCraftable = predicted == null || Reflect.GetBool(predicted, "Craftable", true);

            if (!predictedCraftable)
            {
                object better = FindStartableRecipe(payload, out string why);
                if (better != null)
                {
                    MethodInfo startRecipe = Reflect.Method(payload.GetType(), "StartRecipe", 1);
                    if (startRecipe != null)
                    {
                        try
                        {
                            startRecipe.Invoke(payload, new[] { better });
                            result["recipeAutoSelected"] = Reflect.GetString(better, "Id");
                            result["skippedUnstartable"] = predictedId;
                            return FinishStart(result, payload);
                        }
                        catch (Exception e)
                        {
                            throw new ApiException(500, "StartRecipe threw: " + Reflect.Unwrap(e).Message);
                        }
                    }
                }
                // Nothing better found: fall through and let TryStart report the truth.
                result["unstartableRecipe"] = predictedId;
                if (why != null) result["why"] = why;
            }

            MethodInfo m = Reflect.Method(payload.GetType(), "TryStart", 0);
            if (m == null) throw new ApiException(500, "Situation.TryStart not found");
            try
            {
                m.Invoke(payload, null);
            }
            catch (Exception e)
            {
                throw new ApiException(500, "TryStart threw: " + Reflect.Unwrap(e).Message);
            }

            return FinishStart(result, payload);
        }

        /// <summary>
        /// The first recipe for this verb that is Craftable AND whose requirements the situation
        /// currently satisfies - the one the game should have picked itself.
        /// </summary>
        private static object FindStartableRecipe(object payload, out string why)
        {
            why = null;
            string verbId = Reflect.GetString(payload, "VerbId");
            List<object> candidates = Reflect.ValidRecipesForVerb(verbId);
            if (candidates.Count == 0)
            {
                why = "the compendium lists no recipes for verb '" + verbId + "'";
                return null;
            }

            object aspects = Reflect.AspectsInContextOf(payload);
            object character = Reflect.Protag();

            foreach (object r in candidates)
            {
                if (r == null) continue;
                if (!Reflect.GetBool(r, "Craftable")) continue;
                if (aspects != null && !Reflect.RecipeCanExecute(r, aspects, character)) continue;
                return r;
            }

            why = "no craftable recipe for verb '" + verbId + "' is satisfied by what is in its slots right now";
            return null;
        }

        /// <summary>Fill in the after-state fields and the changed flag, then return the result.</summary>
        private static Dictionary<string, object> FinishStart(Dictionary<string, object> result, object payload)
        {
            result["stateAfter"] = StateOf(payload);
            result["recipeAfter"] = Reflect.GetString(payload, "CurrentRecipeId");
            result["timeRemaining"] = Json.Round(Reflect.GetFloat(payload, "TimeRemaining"));

            // TryStart is a no-op when the current recipe is not craftable. Reporting a bare 200
            // for that misled a driver into retrying the same button forever, so say plainly
            // whether anything moved.
            string before = result["stateBefore"] as string;
            string after = result["stateAfter"] as string;
            string recipeBefore = result["recipeBefore"] as string;
            string recipeAfter = result["recipeAfter"] as string;
            bool changed = !string.Equals(before, after, StringComparison.Ordinal)
                        || !string.Equals(recipeBefore, recipeAfter, StringComparison.Ordinal);

            result["changed"] = changed;
            if (!changed)
            {
                result["success"] = false;
                result["reason"] = after == "Ongoing"
                    ? "the verb is already running"
                    : "the verb did not start: the current recipe (" + (recipeAfter ?? "none")
                      + ") cannot run with what is in its slots";
            }
            else
            {
                result["success"] = true;
            }
            return result;
        }


        /// <summary>
        /// The situation's state as a clean enum name. StateIdentifier is the enum ("Unstarted");
        /// the State property is a state object whose ToString() is the full type name.
        /// </summary>
        private static string StateOf(object situation)
        {
            string identifier = SafeToString(() => Reflect.Get(situation, "StateIdentifier"));
            if (!string.IsNullOrEmpty(identifier)) return identifier;
            return SafeToString(() => Reflect.Get(situation, "State"));
        }

        // --- move: relocate a card ---------------------------------------------------------

        /// <summary>
        /// Move a token to another sphere, animated through a travel itinerary (the same path a
        /// drag takes). Falls back to a direct sphere assignment when no itinerary is available.
        /// </summary>
        public static Dictionary<string, object> Move(string tokenId, string destinationPath, float? x, float? y)
        {
            var result = NewResult("move");

            // Validate everything cheap first: a request with missing arguments should fail with
            // 400 without going anywhere near the sphere tree.
            if (string.IsNullOrEmpty(tokenId)) throw new ApiException(400, "'token' is required");
            if (string.IsNullOrEmpty(destinationPath) && !x.HasValue && !y.HasValue)
                throw new ApiException(400, "'to' (destination sphere path) or x/y is required");

            object token = FindToken(tokenId);
            if (token == null) throw new ApiException(404, "no token with id '" + tokenId + "'");

            object currentSphere = Reflect.Get(token, "Sphere");
            string fromPath = Reflect.SpherePath(currentSphere);
            result["token"] = tokenId;
            result["from"] = fromPath;

            if (string.IsNullOrEmpty(destinationPath) && (x.HasValue || y.HasValue))
            {
                // Position-only move within the current sphere.
                if (currentSphere == null) throw new ApiException(409, "token '" + tokenId + "' is not in any sphere");
                destinationPath = fromPath;
            }

            if (destinationPath == fromPath && !x.HasValue && !y.HasValue)
            {
                result["success"] = true;
                result["noop"] = true;
                result["reason"] = "token already in " + fromPath;
                return result;
            }

            object destination = Reflect.SphereByPath(destinationPath);
            object context = Reflect.MakeContext("PlayerDrag") ?? Reflect.MakeContext("Debug");

            // Same guard as place: an OutputSphere is where a route delivers a card, not where it
            // enters. Moving straight there skipped the recipe/EvictToken chain and left the
            // mansus Ingress stuck, so the map overlay could never close.
            object entry = ResolveEntrySphere(destination, destinationPath, result);
            if (entry != null)
            {
                destination = entry;
                destinationPath = Reflect.SpherePath(entry) ?? destinationPath;
            }

            bool travelled = false;
            string itineraryError = null;
            string how = null;

            // Preferred route: ask the destination for its itinerary and send the token along it.
            if (x.HasValue || y.HasValue)
            {
                try
                {
                    var position = new Vector3(x ?? 0f, y ?? 0f, 0f);
                    MethodInfo getItinerary = FindItineraryMethod(destination, 2);
                    if (getItinerary != null)
                    {
                        object itinerary = getItinerary.Invoke(destination, new[] { token, (object)position });
                        travelled = Depart(token, itinerary, context);
                        if (travelled) how = "itinerary(x,y)";
                    }
                }
                catch (Exception e) { itineraryError = Reflect.Unwrap(e).Message; }
            }

            // No explicit position: let the destination's choreographer choose a free slot.
            // Sending the token along a positionless itinerary drops it at a default spot, which
            // is how collected cards ended up piled in one heap.
            if (!travelled && !x.HasValue && !y.HasValue)
            {
                if (Reflect.PlaceTokenOnSphere(destination, token, context, out how))
                {
                    travelled = true;
                }
            }

            if (!travelled && destinationPath != fromPath)
            {
                try
                {
                    MethodInfo getItinerary = FindItineraryMethod(destination, 1);
                    if (getItinerary != null)
                    {
                        object itinerary = getItinerary.Invoke(destination, new[] { token });
                        travelled = Depart(token, itinerary, context);
                        if (travelled) how = "itinerary";
                    }
                }
                catch (Exception e) { itineraryError = itineraryError ?? Reflect.Unwrap(e).Message; }
            }

            if (!travelled)
            {
                // Last resort: hand the token to the destination sphere directly.
                try
                {
                    travelled = Reflect.TakeToken(destination, token, context, out how);
                    if (!travelled)
                        throw new ApiException(500, "the destination sphere would not take the token"
                            + (itineraryError != null ? " (itinerary: " + itineraryError + ")" : ""));
                }
                catch (ApiException) { throw; }
                catch (Exception e)
                {
                    throw new ApiException(500, "could not move token: " + Reflect.Unwrap(e).Message
                        + (itineraryError != null ? " (itinerary: " + itineraryError + ")" : ""));
                }
            }

            // Merge like cards now sitting together, so a collection does not leave several
            // single-card stacks of the same element side by side.
            result["groupedStacks"] = Reflect.GroupAllStacks(destination);

            result["to"] = Reflect.SpherePath(Reflect.Get(token, "Sphere")) ?? destinationPath;
            result["position"] = PositionOf(token);
            result["viaItinerary"] = travelled;
            result["via"] = how;
            result["success"] = true;
            if (itineraryError != null) result["itineraryWarning"] = itineraryError;
            return result;
        }

        private static MethodInfo FindItineraryMethod(object sphere, int argCount)
        {
            MethodInfo m = Reflect.Method(sphere.GetType(), "GetItineraryFor", argCount);
            if (m != null) return m;
            // Sphere declares it virtual; search base types too.
            Type t = sphere.GetType().BaseType;
            while (t != null)
            {
                m = Reflect.Method(t, "GetItineraryFor", argCount);
                if (m != null) return m;
                t = t.BaseType;
            }
            return null;
        }

        private static bool Depart(object token, object itinerary, object context)
        {
            if (itinerary == null) return false;
            MethodInfo depart = Reflect.Method(itinerary.GetType(), "Depart", 2);
            if (depart == null) return false;
            depart.Invoke(itinerary, new[] { token, context });
            return true;
        }

        // --- click: interact with a card ---------------------------------------------------

        /// <summary>
        /// Perform a token's primary interaction. For a situation that toggles open/closed; for a
        /// card it triggers the game's own interaction rules. Implemented through Token.Select()
        /// plus the payload's InteractWithIncoming-free click path where available.
        /// </summary>
        public static Dictionary<string, object> Click(string tokenId)
        {
            var result = NewResult("click");
            if (string.IsNullOrEmpty(tokenId)) throw new ApiException(400, "'token' is required");

            object token = FindToken(tokenId);
            if (token == null) throw new ApiException(404, "no token with id '" + tokenId + "'");

            object payload = Reflect.Get(token, "Payload");
            result["token"] = tokenId;
            result["payloadType"] = payload == null ? null : payload.GetType().Name;

            // Situations expose OpenAt/Close; use them so the window state stays consistent.
            if (payload != null && payload.GetType().Name == "Situation")
            {
                bool isOpen = Reflect.GetBool(payload, "IsOpen");
                MethodInfo m = Reflect.Method(payload.GetType(), isOpen ? "Close" : "OpenAt", isOpen ? 0 : 1);
                if (m != null)
                {
                    object[] args = isOpen
                        ? new object[0]
                        : new[] { Reflect.Get(token, "Location") };
                    m.Invoke(payload, args);
                    result["action"] = isOpen ? "close" : "open";
                    result["success"] = true;
                    return result;
                }
            }

            MethodInfo select = Reflect.Method(token.GetType(), "Select", 0);
            if (select != null)
            {
                select.Invoke(token, null);
                MethodInfo deselect = Reflect.Method(token.GetType(), "Deselect", 0);
                if (deselect != null) deselect.Invoke(token, null);
                result["action"] = "select";
                result["success"] = true;
                return result;
            }

            throw new ApiException(501, "no click interaction available for this token");
        }

        // --- menu: start or resume a game without clicking anything -------------------------

        /// <summary>
        /// Which scene the game is showing. Served by GET /screen so a caller can decide for
        /// itself whether a start/resume makes sense, instead of guessing from heuristics.
        /// </summary>
        public static Dictionary<string, object> Screen()
        {
            var result = NewResult("screen");
            object stageHand = Reflect.WatchmanGet(Reflect.FindType("SecretHistories.Services.StageHand"));

            string scene = null;
            bool menuActive = false;
            bool uhoActive = false;

            if (stageHand != null)
            {
                MethodInfo foremost = Reflect.Method(stageHand.GetType(), "GetForemostScene", 0);
                if (foremost != null)
                {
                    try
                    {
                        object s = foremost.Invoke(stageHand, null);
                        if (s != null) scene = Reflect.GetString(s, "name") ?? s.ToString();
                    }
                    catch { }
                }

                menuActive = SceneIsActive(stageHand, "Menu");
                uhoActive = SceneIsActive(stageHand, "UHO");
            }

            result["scene"] = scene;
            result["menuSceneActive"] = menuActive;
            result["errorSceneActive"] = uhoActive;
            result["tabletopHasCards"] = GameStateSerializer.HasActiveGame();
            result["success"] = true;
            return result;
        }

        private static bool SceneIsActive(object stageHand, string sceneName)
        {
            MethodInfo m = Reflect.Method(stageHand.GetType(), "SceneIsActive", 1);
            if (m == null) return false;
            try
            {
                object r = m.Invoke(stageHand, new object[] { sceneName });
                return r is bool && (bool)r;
            }
            catch { return false; }
        }

        /// <summary>True when the menu screen is not blocking input (its buttons would work).</summary>
        private static bool ControllerAcceptsInput(object controller)
        {
            object v = Reflect.Get(controller, "canTakeInput");
            if (v == null) return true;                 // unknown: let the game decide
            try { return Convert.ToBoolean(v); } catch { return true; }
        }

        /// <summary>
        /// Every legacy available to start, with the one the game's own "default legacy" path
        /// would pick flagged. Served by GET /legacies.
        /// </summary>
        public static Dictionary<string, object> ListLegacies()
        {
            var result = NewResult("legacies");
            var list = new List<object>();
            List<KeyValuePair<string, string>> legacies;
            try { legacies = Reflect.Legacies(); }
            catch (ApiException) { throw; }

            string first = legacies.Count > 0 ? legacies[0].Key : null;
            foreach (var kv in legacies)
            {
                list.Add(new Dictionary<string, object>
                {
                    { "id", kv.Key },
                    { "label", kv.Value },
                    { "isDefault", kv.Key == first }
                });
            }

            result["count"] = list.Count;
            result["defaultLegacyId"] = first;
            result["legacies"] = list;
            result["inGame"] = Reflect.HasActiveGame();
            result["success"] = true;
            return result;
        }

        /// <summary>
        /// Start a fresh game. With no legacyId this mirrors the menu's BeginGameWithDefaultLegacy
        /// (the first entry in the compendium); with one it mirrors the legacy button, which ends
        /// up in MenuScreenController.BeginNewSaveWithSpecifiedLegacy.
        /// </summary>
        public static Dictionary<string, object> NewGame(string legacyId)
        {
            var result = NewResult("newgame");

            // Deliberately no "a game is already in progress" guard. Any heuristic for that
            // misjudged the menu (where FucineRoot and ~/cardsexhibit still exist), and the game
            // guards this itself through canTakeInput - the called method simply does nothing
            // when input is blocked. So call it and report what actually happened.
            object controller = Reflect.MenuController();

            if (string.IsNullOrEmpty(legacyId))
            {
                Invoke(controller, "BeginGameWithDefaultLegacy", 0);
                result["legacyId"] = "default";
                result["note"] = "used the game's default legacy (the first compendium entry)";
            }
            else
            {
                // Fail before touching the game if the legacy is not real.
                bool known = false;
                foreach (var kv in Reflect.Legacies()) { if (kv.Key == legacyId) { known = true; break; } }
                if (!known) throw new ApiException(404, "no legacy with id '" + legacyId + "' (GET /legacies for the list)");

                Invoke(controller, "BeginNewSaveWithSpecifiedLegacy", 1, legacyId);
                result["legacyId"] = legacyId;
            }

            result["inputAccepted"] = ControllerAcceptsInput(controller);
            result["success"] = true;
            if (!(bool)result["inputAccepted"])
                result["warning"] = "the menu was not accepting input, so the game most likely did nothing; "
                    + "check GET /screen and call again once the menu is idle";

            object existingNote;
            string prefix = result.TryGetValue("note", out existingNote) && existingNote != null
                ? existingNote + "; "
                : "";
            result["note"] = prefix + "loading runs asynchronously - poll GET /state until it answers 200";
            return result;
        }

        /// <summary>
        /// Resume the existing save.
        ///
        /// Prefers MenuScreenController.ContinueGame (what the Continue button runs). When the
        /// menu scene is not loaded - i.e. the caller is already on the tabletop - it falls back
        /// to the same two steps that method performs, so resume works from anywhere.
        /// </summary>
        public static Dictionary<string, object> ContinueGame()
        {
            var result = NewResult("continue");

            object controller = Reflect.MenuControllerOrNull();
            if (controller != null)
            {
                Invoke(controller, "ContinueGame", 0);
                result["via"] = "MenuScreenController.ContinueGame";
                result["inputAccepted"] = ControllerAcceptsInput(controller);
                if (!(bool)result["inputAccepted"])
                    result["warning"] = "the menu was not accepting input, so the game most likely did nothing";
            }
            else
            {
                // No menu in the scene: do directly what ContinueGame would have done.
                object provider = Reflect.NewDefaultPersistenceProvider();
                Reflect.LoadGameOnTabletop(provider);
                result["via"] = "StageHand.LoadGameOnTabletop(DefaultGamePersistenceProvider)";
                result["note"] = "the menu was not loaded, so the continue flow was reproduced directly";
            }

            result["success"] = true;
            object existing;
            string prefix = result.TryGetValue("note", out existing) && existing != null ? existing + "; " : "";
            result["note"] = prefix + "loading runs asynchronously - poll GET /state until it answers 200";
            return result;
        }

        private static void Invoke(object target, string method, int argCount, params object[] args)
        {
            MethodInfo m = Reflect.Method(target.GetType(), method, argCount);
            if (m == null) throw new ApiException(500, target.GetType().Name + "." + method + "() not found in this game build");
            try
            {
                m.Invoke(target, args);
            }
            catch (Exception e)
            {
                throw new ApiException(500, method + " threw: " + Reflect.Unwrap(e).Message);
            }
        }

        // --- conclude / collect ------------------------------------------------------------

        /// <summary>
        /// Close a verb's window - the X in its top corner.
        ///
        /// Situation.Conclude() is what the game's own "collect all" hotkey calls on an open
        /// situation, and an open window keeps covering the board, so a driver that opens verbs
        /// should close them too.
        /// </summary>
        public static Dictionary<string, object> Conclude(string situationTokenId)
        {
            var result = NewResult("conclude");
            if (string.IsNullOrEmpty(situationTokenId)) throw new ApiException(400, "'token' is required");

            object token = FindToken(situationTokenId);
            if (token == null) throw new ApiException(404, "no token with id '" + situationTokenId + "'");

            object payload = Reflect.Get(token, "Payload");
            if (payload == null || payload.GetType().Name != "Situation")
                throw new ApiException(409, "token '" + situationTokenId + "' is not a Situation");
            if (!Reflect.GetBool(payload, "IsOpen"))
            {
                result["success"] = true;
                result["alreadyClosed"] = true;
                result["note"] = "the window was already closed";
                return result;
            }

            // Situation.Conclude() begins with:
            //     if (!State.IsConcludable) return;
            // A verb that is still running cannot be concluded, and in that case Conclude()
            // silently queues nothing at all - the window then stays open forever. Reporting a
            // cheerful 200 there (and a misleading isOpenAfter) sent the driver round in circles,
            // so check first and say so plainly.
            object state = Reflect.Get(payload, "State");
            bool concludable = true;
            if (state != null)
            {
                MethodInfo canConclude = Reflect.Method(state.GetType(), "IsConcludable", 0);
                if (canConclude != null)
                {
                    try
                    {
                        object r = canConclude.Invoke(state, null);
                        if (r is bool) concludable = (bool)r;
                    }
                    catch { }
                }
            }

            if (!concludable)
            {
                result["token"] = situationTokenId;
                result["state"] = StateOf(payload);
                result["concludable"] = false;
                result["queued"] = false;
                result["success"] = false;
                result["reason"] = "a verb that is still " + (StateOf(payload) ?? "running")
                    + " cannot be concluded; its window can only be closed once the recipe finishes. "
                    + "Poll /state until this situation reports Complete, then conclude again.";
                return result;
            }

            MethodInfo m = Reflect.Method(payload.GetType(), "Conclude", 0);
            if (m == null) throw new ApiException(500, "Situation.Conclude not found");
            try { m.Invoke(payload, null); }
            catch (Exception e)
            {
                // Fall back to the plain window close.
                MethodInfo close = Reflect.Method(payload.GetType(), "Close", 0);
                if (close == null) throw new ApiException(500, "Conclude threw: " + Reflect.Unwrap(e).Message);
                try { close.Invoke(payload, null); }
                catch (Exception e2) { throw new ApiException(500, "could not close the window: " + Reflect.Unwrap(e2).Message); }
            }

            result["token"] = situationTokenId;
            result["state"] = StateOf(payload);
            result["concludable"] = true;

            // The commands are QUEUED (EvictOutputCommand then ConcludeCommand) and run on a later
            // beat, so nothing has happened yet when this returns. Report what was requested and
            // let the caller confirm through /state's openWindows.
            result["queued"] = true;
            result["note"] = "Conclude queues its commands; re-read /state and check openWindows to confirm the window shut";
            result["success"] = true;
            return result;
        }

        /// <summary>
        /// Take everything out of a situation's output frame and lay it on the tabletop.
        ///
        /// This is what "collect" means mechanically. Cards are placed at free positions by the
        /// tabletop choreographer rather than dumped at one spot, and like cards are grouped
        /// afterwards, so a collection does not arrive as a single illegible heap.
        /// </summary>
        public static Dictionary<string, object> Collect(string situationTokenId)
        {
            var result = NewResult("collect");
            object situationToken = null;

            if (string.IsNullOrEmpty(situationTokenId))
            {
                // No particular verb: collect from every situation that has an output frame.
                // Picking just the first one left other verbs' output untouched while still
                // reporting success, which hid the fact that nothing had been taken.
                situationToken = null;
            }
            else
            {
                situationToken = FindToken(situationTokenId);
            }

            var tabletop = Reflect.SphereByPath("~/tabletop");
            object context = Reflect.MakeContext("PlayerDrag") ?? Reflect.MakeContext("Debug");

            var collected = new List<object>();
            var failures = new List<object>();

            // Gather the candidate output tokens first: moving tokens mutates the spheres we
            // would otherwise be walking.
            var candidates = new List<object>();
            foreach (var kv in AllTokens())
            {
                object payload = Reflect.Get(kv.Value, "Payload");
                if (payload == null || payload.GetType().Name != "Situation") continue;
                if (situationToken != null && !ReferenceEquals(kv.Value, situationToken)) continue;

                foreach (object dominion in Reflect.ToList(Reflect.Get(payload, "Dominions")))
                {
                    foreach (object s in Reflect.ToList(Reflect.Get(dominion, "Spheres")))
                    {
                        if (s.GetType().Name != "OutputSphere") continue;
                        foreach (object t in Reflect.ToList(Reflect.Get(s, "Tokens"))) candidates.Add(t);
                    }
                }
            }

            // Preferred route: let the GAME collect. Situation.Conclude() queues
            // EvictOutputCommand, whose Execute does:
            //     var tokens = situation.GetElementTokens(16);
            //     if (!trySendTokensToPath(sphere, situation.OutputPath, tokens))
            //         justEvictTokens(tokens);
            //     SoundManager.PlaySfx("SituationCollectAll");
            // "SituationCollectAll" is the sound of the game's own collect button, so this is
            // literally that button. The cards then go to situation.OutputPath and are laid out by
            // the game, which is why the result looks right by definition.
            //
            // Conclude refuses while the verb is still running (State.IsConcludable is false), so
            // this is simply not available for an in-progress recipe; the manual path below covers
            // that case.
            if (candidates.Count > 0)
            {
                var evicted = new List<object>();
                var stillPresent = new List<object>();

                var allTokens = new List<KeyValuePair<string, object>>(AllTokens());
                foreach (var kv in allTokens)
                {
                    object payload = Reflect.Get(kv.Value, "Payload");
                    if (payload == null || payload.GetType().Name != "Situation") continue;
                    if (situationToken != null && !ReferenceEquals(kv.Value, situationToken)) continue;
                    if (!IsConcludable(payload)) continue;
                    if (!HasOutputTokens(payload)) continue;

                    try
                    {
                        MethodInfo conclude = Reflect.Method(payload.GetType(), "Conclude", 0);
                        if (conclude != null)
                        {
                            conclude.Invoke(payload, null);
                            result["viaConclude"] = true;
                        }
                    }
                    catch { }
                }

                if (result.ContainsKey("viaConclude"))
                {
                    // The eviction is queued, so give the game a moment to run it before judging.
                    System.Threading.Thread.Sleep(400);

                    foreach (object t in candidates)
                    {
                        if (IsStillInAnOutputSphere(t)) stillPresent.Add(Reflect.GetString(t, "PayloadId"));
                        else evicted.Add(new Dictionary<string, object>
                        {
                            { "token", Reflect.GetString(t, "PayloadId") },
                            { "via", "game-evict(Conclude)" }
                        });
                    }

                    if (evicted.Count > 0)
                    {
                        collected.AddRange(evicted);
                        result["note"] = "the game evicted these itself (SituationCollectAll); it owns the layout";
                    }
                }
            }

            foreach (object t in candidates)
            {
                string id = Reflect.GetString(t, "PayloadId");

                // Skip anything the game already collected for us through Conclude above.
                if (!IsStillInAnOutputSphere(t)) continue;

                // Adopt the token onto the tabletop and let its choreographer pick a free slot.
                // Accepting alone leaves every collected card at the same default position, which
                // is exactly the heap this endpoint exists to avoid.
                string how;
                bool placed = Reflect.PlaceTokenOnSphere(tabletop, t, context, out how);

                if (placed) collected.Add(new Dictionary<string, object> { { "token", id }, { "via", how } });
                else failures.Add(id);
            }

            result["situation"] = situationTokenId ?? "all";
            result["collected"] = collected;
            result["count"] = collected.Count;
            if (failures.Count > 0) result["failed"] = failures;
            result["groupedStacks"] = Reflect.GroupAllStacks(tabletop);

            // Report honestly: candidates found but none taken is a failure, not a quiet success.
            result["candidates"] = candidates.Count;
            result["success"] = failures.Count == 0;
            if (failures.Count > 0)
                result["reason"] = "some of the " + candidates.Count + " cards in output frames could not be taken by the tabletop";
            else if (candidates.Count == 0)
                result["note"] = "nothing was waiting in any verb's output frame";

            List<object> swept = RetireOrphanIngress();
            if (swept.Count > 0) result["retiredOrphanIngress"] = swept;

            return result;
        }

        /// <summary>
        /// Close every situation window, exactly as the game's own Meniscate does.
        ///
        /// Meniscate.CloseAllSituationWindowsExcept is:
        ///     foreach (var s in HornedAxe.GetRegisteredSituations()) s.Close();
        /// and Situation.Close() simply sets IsOpen = false and raises the event the window
        /// listens to. That is deliberately different from Conclude():
        ///
        ///   * Conclude() begins with "if (!State.IsConcludable) return;" - it only works once a
        ///     verb's recipe has finished.
        ///   * Close() has no such gate, so it shuts a window whatever the verb is doing.
        ///
        /// This matters because the game re-opens a default window by itself: finish a work verb
        /// and a fresh one appears, covering the board again. A driver that only concludes once
        /// therefore keeps losing the screen. Calling this every cycle keeps the tabletop clear.
        /// </summary>
        public static Dictionary<string, object> CloseWindows(string exceptTokenId)
        {
            var result = NewResult("closewindows");

            object except = null;
            if (!string.IsNullOrEmpty(exceptTokenId))
            {
                object t = FindToken(exceptTokenId);
                if (t == null) throw new ApiException(404, "no token with id '" + exceptTokenId + "'");
                except = Reflect.Get(t, "Payload");
            }

            object hornedAxe = Reflect.HornedAxe();
            var closed = new List<object>();
            var before = new List<object>();

            foreach (object s in Reflect.ToList(Reflect.RegisteredSituations(hornedAxe)))
            {
                if (s == null) continue;
                if (except != null && ReferenceEquals(s, except)) continue;

                bool wasOpen = Reflect.GetBool(s, "IsOpen");
                string id = Reflect.GetString(s, "SituationId") ?? Reflect.GetString(s, "VerbId");
                if (wasOpen) before.Add(id);

                // Prefer the engine's own sweep, which handles the whole set in one go; fall back
                // to calling Close() per situation.
                MethodInfo close = Reflect.Method(s.GetType(), "Close", 0);
                if (close != null)
                {
                    try
                    {
                        close.Invoke(s, null);
                        if (wasOpen) closed.Add(id);
                    }
                    catch { }
                }
            }

            result["wereOpen"] = before;
            result["closedCount"] = closed.Count;
            result["closed"] = closed;
            result["success"] = true;
            if (closed.Count == 0) result["note"] = "no situation window was open";
            result["note2"] = "the game can re-open a default window by itself, so call this each cycle rather than once";
            return result;
        }

        // --- diagnose ----------------------------------------------------------------------

        /// <summary>
        /// Read-only dump of how the tabletop looks from inside the placement code.
        ///
        /// The grid-row placement kept computing x=0 because it believed the row was empty, while
        /// /state could see the cards perfectly well. This reports the raw material - what the
        /// sphere's Tokens yields, and the exact CLR type of each Position component - so the
        /// difference is visible instead of guessed at.
        /// </summary>
        public static Dictionary<string, object> Diagnose()
        {
            var result = NewResult("diagnose");
            object tabletop = Reflect.SphereByPath("~/tabletop");

            var raw = new List<object>();
            object tokensObj = Reflect.Get(tabletop, "Tokens");
            result["tokensPropertyType"] = tokensObj == null ? "null" : tokensObj.GetType().FullName;

            List<object> tokens = Reflect.ToList(tokensObj);
            result["tokenCountViaToList"] = tokens.Count;

            foreach (object t in tokens)
            {
                var entry = new Dictionary<string, object>();
                entry["tokenType"] = t == null ? "null" : t.GetType().Name;
                entry["payloadId"] = Reflect.GetString(t, "PayloadId");
                entry["tokenId"] = Reflect.GetString(t, "Id");

                object location = Reflect.Get(t, "Location");
                object local = location == null ? null : Reflect.Get(location, "LocalPosition");
                entry["locationType"] = location == null ? "null" : location.GetType().Name;
                entry["localPositionType"] = local == null ? "null" : local.GetType().FullName;

                float px, py;
                bool ok = Reflect.TryLocalPosition(t, out px, out py);
                entry["readable"] = ok;
                entry["x"] = ok ? (object)px : null;
                entry["y"] = ok ? (object)py : null;
                raw.Add(entry);
            }

            result["tokens"] = raw;

            // What the grid helpers manage to read.
            Type meniscateType = Reflect.FindType("SecretHistories.UI.Meniscate");
            result["meniscateTypeFound"] = meniscateType != null;
            result["meniscateInScene"] = Reflect.FindComponentInScene(meniscateType) != null;

            result["success"] = true;
            return result;
        }

        // --- collect helpers ---------------------------------------------------------------

        /// <summary>True when Situation.State.IsConcludable - i.e. the verb has finished.</summary>
        private static bool IsConcludable(object situation)
        {
            if (situation == null) return false;
            try
            {
                object state = Reflect.Get(situation, "State");
                if (state == null) return false;
                MethodInfo m = Reflect.Method(state.GetType(), "IsConcludable", 0);
                if (m == null) return false;
                object r = m.Invoke(state, null);
                return r is bool && (bool)r;
            }
            catch { return false; }
        }

        /// <summary>True when any of the situation's OutputSpheres still holds a card.</summary>
        private static bool HasOutputTokens(object situation)
        {
            foreach (object s in OutputSpheres(situation))
            {
                if (Reflect.ToList(Reflect.Get(s, "Tokens")).Count > 0) return true;
            }
            return false;
        }

        private static IEnumerable<object> OutputSpheres(object situation)
        {
            var found = new List<object>();
            if (situation == null) return found;
            try
            {
                foreach (object dominion in Reflect.ToList(Reflect.Get(situation, "Dominions")))
                {
                    foreach (object s in Reflect.ToList(Reflect.Get(dominion, "Spheres")))
                    {
                        if (s != null && s.GetType().Name == "OutputSphere") found.Add(s);
                    }
                }
            }
            catch { }
            return found;
        }

        /// <summary>
        /// Whether a token is still sitting in some situation's output frame. Used to tell what the
        /// game collected on its own from what is still waiting.
        /// </summary>
        private static bool IsStillInAnOutputSphere(object token)
        {
            if (token == null) return false;
            try
            {
                foreach (var kv in AllTokens())
                {
                    object payload = Reflect.Get(kv.Value, "Payload");
                    if (payload == null || payload.GetType().Name != "Situation") continue;
                    foreach (object s in OutputSpheres(payload))
                    {
                        foreach (object t in Reflect.ToList(Reflect.Get(s, "Tokens")))
                        {
                            if (ReferenceEquals(t, token)) return true;
                        }
                    }
                }
            }
            catch { }
            return false;
        }

        // --- lookups -----------------------------------------------------------------------

        /// <summary>
        /// Find a token anywhere in the sphere tree by its payload id.
        ///
        /// Deliberately does NOT trust Sphere.GetTokenById()'s return value on its own: when the
        /// game cannot find a token it hands back a NullToken (whose payload type is
        /// NullElementStack) rather than null, so a plain null check passes and every lookup
        /// "succeeds" with a useless token. The token lists are the source of truth here, and
        /// GetTokenById is only consulted as a last resort after validating its result.
        /// </summary>
        public static object FindToken(string tokenId)
        {
            if (string.IsNullOrEmpty(tokenId)) return null;

            foreach (object sphere in Reflect.AllSpheres())
            {
                // Primary: walk the sphere's own token list.
                foreach (object t in Reflect.ToList(Reflect.Get(sphere, "Tokens")))
                {
                    if (IsSameToken(t, tokenId)) return t;
                }

                // Fallback: the sphere's own lookup, but only if it returned something real.
                MethodInfo byId = Reflect.Method(sphere.GetType(), "GetTokenById", 1);
                if (byId != null)
                {
                    try
                    {
                        object t = byId.Invoke(sphere, new object[] { tokenId });
                        if (t != null && !IsNullToken(t) && IsSameToken(t, tokenId)) return t;
                    }
                    catch { }
                }
            }
            return null;
        }

        private static bool IsSameToken(object token, string tokenId)
        {
            if (token == null) return false;
            if (IsNullToken(token)) return false;

            if (string.Equals(Reflect.GetString(token, "PayloadId"), tokenId, StringComparison.Ordinal)) return true;

            // Some manifestations are addressed by their path part instead of the payload id.
            MethodInfo matches = Reflect.Method(token.GetType(), "MatchesPathTokenId", 1);
            if (matches != null)
            {
                try
                {
                    object r = matches.Invoke(token, new object[] { tokenId });
                    if (r is bool && (bool)r) return true;
                }
                catch { }
            }
            return false;
        }

        /// <summary>True for the game's NullToken / NullElementStack stand-ins.</summary>
        private static bool IsNullToken(object token)
        {
            if (token == null) return true;
            string typeName = token.GetType().Name;
            if (typeName == "NullToken") return true;

            object payload = Reflect.Get(token, "Payload");
            if (payload == null) return true;
            string payloadType = payload.GetType().Name;
            return payloadType == "NullElementStack" || payloadType.StartsWith("Null", StringComparison.Ordinal);
        }

        /// <summary>All tokens on the board, as (spherePath, token) pairs.</summary>
        public static IEnumerable<KeyValuePair<string, object>> AllTokens()
        {
            foreach (object sphere in Reflect.AllSpheres())
            {
                string path = Reflect.SpherePath(sphere);
                foreach (object t in Reflect.ToList(Reflect.Get(sphere, "Tokens")))
                {
                    yield return new KeyValuePair<string, object>(path, t);
                }
            }
        }

        public static object PositionOf(object token)
        {
            try
            {
                object location = Reflect.Get(token, "Location");
                object local = Reflect.Get(location, "LocalPosition");
                if (local is Vector3) return Json.Vec3ToJson((Vector3)local);
            }
            catch { }
            return null;
        }

        private static Dictionary<string, object> NewResult(string action)
        {
            return new Dictionary<string, object>
            {
                { "success", false },
                { "action", action }
            };
        }

        private static string SafeToString(Func<object> f)
        {
            try
            {
                object o = f();
                return o == null ? null : o.ToString();
            }
            catch { return null; }
        }
    }
}
