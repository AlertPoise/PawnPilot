using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text.Json;

namespace PawnPilot
{
    public static class BusinessObservationTests
    {
        private sealed class Identity { }

        public static void Run(Action<string, bool> check)
        {
            var input = Sample();
            int readerCalls = 0;
            var projection = new BusinessProjection();
            var notReady = projection.Capture("w1", false, () => { readerCalls++; return input; });
            check("business_not_ready_does_not_read", readerCalls == 0 && notReady.details.status == "unavailable");
            check("business_unavailable_projection_emits_valid_wire_v3", BusinessWire.Validate(notReady));

            var first = projection.Capture("w1", true, () => input);
            string firstCustomer = first.customer_ref;
            string firstItem = first.details.items[0].item_ref;
            check("business_visible_complete_container_gets_opaque_refs", firstCustomer != null && firstItem != null &&
                first.details.containers[0].completeness == "complete");
            check("business_projection_emits_valid_wire_v3", BusinessWire.Validate(first));
            check("business_quote_stays_unverified_without_binding", first.details.quote.status == "unverified" &&
                first.details.quote.rows[0].item_ref == null && first.details.quote.rows[0].amount == null &&
                first.details.quote.displayed_total == 1234 && first.offer_ref == null);
            var assertedBinding = Sample(); assertedBinding.Quote.BindingVerified = true;
            assertedBinding.Quote.Status = "complete"; assertedBinding.Quote.Mode = "normal";
            var downgraded = new BusinessProjection().Capture("w1", true, () => assertedBinding);
            check("business_input_flag_cannot_mint_verified_offer", BusinessWire.Validate(downgraded) &&
                downgraded.details.quote.status == "unverified" && downgraded.offer_ref == null &&
                !downgraded.details.eligibility.purchase_enabled);

            var newWorld = projection.Capture("w2", true, () => input);
            check("business_world_change_invalidates_same_object_refs", newWorld.customer_ref != firstCustomer &&
                newWorld.details.items[0].item_ref != firstItem);

            object oldCustomerIdentity = input.Customer.Identity;
            input.Customer.Identity = new Identity();
            var replaced = projection.Capture("w2", true, () => input);
            check("business_customer_replacement_invalidates_item_ref", !ReferenceEquals(oldCustomerIdentity, input.Customer.Identity) &&
                replaced.customer_ref != newWorld.customer_ref && replaced.details.items[0].item_ref != newWorld.details.items[0].item_ref);

            input.Customer.Presence = "absent";
            input.Customer.Identity = null;
            input.Customer.Phase = "leaving";
            var absent = projection.Capture("w2", true, () => input);
            check("business_absent_customer_keeps_independent_inventory_refs", absent.customer_ref == null && absent.details.items.Count == 1 &&
                absent.details.customer.arrived == null && absent.details.customer.phase == "leaving");
            input.Customer.Identity = new Identity();
            input.Customer.Presence = "present";
            var returned = projection.Capture("w2", true, () => input);
            check("business_customer_reappearance_mints_fresh_refs", returned.customer_ref != replaced.customer_ref &&
                returned.details.items[0].item_ref != replaced.details.items[0].item_ref);

            string priorItem = returned.details.items[0].item_ref;
            input.Containers[0].Items[0].Units++;
            var quantityChanged = projection.Capture("w2", true, () => input);
            check("business_quantity_change_invalidates_item_ref", quantityChanged.details.items[0].item_ref != priorItem);
            priorItem = quantityChanged.details.items[0].item_ref;
            var movedItem = input.Containers[0].Items[0];
            input.Containers[0].Items = Array.Empty<BusinessItemInput>();
            input.Containers[1].Items = new[] { movedItem };
            var moved = projection.Capture("w2", true, () => input);
            check("business_container_move_preserves_instance_ref", moved.details.items[0].container == "back" &&
                moved.details.items[0].item_ref == priorItem);
            input.Containers[0].Items = new[] { movedItem };
            input.Containers[1].Items = Array.Empty<BusinessItemInput>();
            var movedBack = projection.Capture("w2", true, () => input);
            priorItem = movedBack.details.items[0].item_ref;
            input.Containers[0].Completeness = "incomplete";
            input.Containers[0].Reason = "member_visibility_unverified";
            var incomplete = projection.Capture("w2", true, () => input);
            check("business_incomplete_container_keeps_only_observed_visible_refs", incomplete.details.containers[0].item_refs.Count == 1 &&
                incomplete.details.items.Count == 1 && incomplete.details.items[0].item_ref != null &&
                incomplete.details.containers[0].completeness == "incomplete");
            input.Containers[0].Completeness = "complete";
            input.Containers[0].Reason = null;
            var completeAgain = projection.Capture("w2", true, () => input);
            check("business_incomplete_to_complete_preserves_visible_identity_ref", completeAgain.details.items[0].item_ref == priorItem);

            var other = Sample();
            other.Customer.Identity = input.Customer.Identity;
            var otherRef = new BusinessProjection().Capture("same", true, () => other);
            check("business_initial_ref_is_issued", otherRef.customer_ref != null);

            var transitionProjection = new BusinessProjection();
            var transitionA = transitionProjection.Capture("w", true, () => other);
            other.Customer.DialogueIdentity = new Identity();
            other.Customer.DialogueStage = "choices";
            other.Customer.OptionIdentities = new object[] { new Identity(), new Identity() };
            other.Customer.OptionTexts = new[] { "Buy", "Leave" };
            other.Customer.DialogueText = "A visible customer line.";
            other.Customer.IsTyping = false;
            other.Customer.WaitingForAdvance = false;
            var transitionB = transitionProjection.Capture("w", true, () => other);
            string oldDialogue = transitionB.details.customer.dialogue_ref;
            string oldOption = transitionB.details.customer.option_refs[0];
            bool resolvesCurrentCustomer = transitionProjection.TryResolveCustomer("w", transitionB.customer_ref, out var resolvedCustomer) &&
                ReferenceEquals(resolvedCustomer, other.Customer.Identity);
            bool resolvesCurrentDialogue = transitionProjection.TryResolveDialogue("w", transitionB.customer_ref, oldDialogue, out var resolvedDialogue) &&
                ReferenceEquals(resolvedDialogue, other.Customer.DialogueIdentity);
            bool resolvesCurrentOption = transitionProjection.TryResolveOption("w", transitionB.customer_ref, oldDialogue, oldOption, out var resolvedOption) &&
                ReferenceEquals(resolvedOption, other.Customer.OptionIdentities[0]);
            check("business_refs_resolve_to_current_opaque_identities", resolvesCurrentCustomer && resolvesCurrentDialogue && resolvesCurrentOption);
            other.Customer.DialogueStage = "waiting";
            other.Customer.OptionIdentities = Array.Empty<object>();
            other.Customer.OptionTexts = Array.Empty<string>();
            other.Customer.DialogueText = "The next visible line.";
            other.Customer.IsTyping = false;
            other.Customer.WaitingForAdvance = true;
            var transitionC = transitionProjection.Capture("w", true, () => other);
            check("business_dialogue_transition_reissues_refs", oldDialogue != transitionC.details.customer.dialogue_ref &&
                transitionC.details.customer.option_refs.Count == 0 &&
                !transitionProjection.TryResolveDialogue("w", transitionB.customer_ref, oldDialogue, out _));
            other.Customer.DialogueStage = "choices";
            other.Customer.OptionIdentities = new object[] { new Identity(), new Identity() };
            other.Customer.OptionTexts = new[] { "Accept", "Decline" };
            var transitionD = transitionProjection.Capture("w", true, () => other);
            check("business_quote_and_dialogue_revision_changes_are_observed", transitionA.details.quote.status == "unverified" &&
                transitionD.details.customer.option_refs[0] != oldOption &&
                transitionD.details.customer.dialogue_text == "The next visible line." &&
                transitionD.details.customer.options[0].text == "Accept");
            other.Quote.DisplayedTotal = 987;
            var quoteRevision = transitionProjection.Capture("w", true, () => other);
            check("business_visible_quote_total_revision_is_reflected_without_authority", quoteRevision.details.quote.displayed_total == 987 &&
                quoteRevision.details.quote.status == "unverified" && quoteRevision.offer_ref == null);

            var collectionInput = Sample();
            collectionInput.Containers[1].Items = new[] { new BusinessItemInput { Identity = new Identity(), Units = 2, Owned = false } };
            var collectionProjection = new BusinessProjection();
            var collectionA = collectionProjection.Capture("w", true, () => collectionInput);
            check("business_unverified_quote_has_no_action_collection_fingerprint", collectionA.collection_fingerprint == null &&
                collectionA.details.quote.status == "unverified" && collectionA.offer_ref == null);

            var completeTrade = CompleteTradeSample();
            var completeTradeProjection = new BusinessProjection();
            var boundTrade = completeTradeProjection.Capture("trade-world", true, () => completeTrade);
            check("schema2_binds_normal_total_to_nonowned_back_subset_with_global_ui_unverified",
                BusinessWire.Validate(boundTrade) && boundTrade.details.schema_version == 2 &&
                boundTrade.details.quote.status == "complete" && boundTrade.details.quote.mode == "normal" &&
                boundTrade.details.quote.displayed_total == 1234 && boundTrade.details.quote.wholesale_total == 900 &&
                boundTrade.details.ui.status == "unverified" && boundTrade.item_refs.Count == 2 &&
                boundTrade.details.containers[1].item_refs.Count == 3 && boundTrade.details.items.Count == 4 &&
                boundTrade.details.items[2].owned && boundTrade.details.front_collection_fingerprint != null);
            bool purchaseResolved = completeTradeProjection.TryResolvePurchase("trade-world", boundTrade.customer_ref,
                boundTrade.offer_ref, boundTrade.collection_fingerprint, out var purchaseObjects, out var purchaseRefs,
                out int purchaseAmount);
            bool frontResolved = completeTradeProjection.TryResolveFrontCollection("trade-world", boundTrade.customer_ref,
                boundTrade.details.front_collection_fingerprint, out var frontObjects, out var frontRefs);
            check("schema2_runtime_purchase_and_prepare_resolvers_return_current_copies", purchaseResolved &&
                purchaseAmount == 1234 && purchaseObjects.Length == 2 && purchaseRefs.Length == 2 &&
                frontResolved && frontObjects.Length == 1 && frontRefs.Length == 1);
            bool backRefResolved = completeTradeProjection.TryGetItemReference("trade-world", "back",
                completeTrade.Containers[1].Items[0].Identity, out string resolvedBackRef);
            check("schema2_capacity_identity_reverse_mapping_is_world_and_container_scoped", backRefResolved &&
                resolvedBackRef == boundTrade.details.containers[1].item_refs[0] &&
                !completeTradeProjection.TryGetItemReference("other-world", "back",
                    completeTrade.Containers[1].Items[0].Identity, out _) &&
                !completeTradeProjection.TryGetItemReference("trade-world", "player",
                    completeTrade.Containers[1].Items[0].Identity, out _));
            purchaseObjects[0] = null; purchaseRefs[0] = "changed";
            check("schema2_resolver_outputs_are_detached", completeTradeProjection.TryResolvePurchase("trade-world",
                boundTrade.customer_ref, boundTrade.offer_ref, boundTrade.collection_fingerprint,
                out var purchaseObjectsAgain, out var purchaseRefsAgain, out _) &&
                purchaseObjectsAgain[0] != null && purchaseRefsAgain[0] != "changed");

            string boundFingerprint = boundTrade.collection_fingerprint;
            string boundOffer = boundTrade.offer_ref;
            completeTrade.Quote.DisplayedTotal++;
            var repricedTrade = completeTradeProjection.Capture("trade-world", true, () => completeTrade);
            check("schema2_normal_price_change_invalidates_offer_and_collection_refs",
                repricedTrade.collection_fingerprint != boundFingerprint && repricedTrade.offer_ref != boundOffer);

            completeTrade.Customer.MultiBuyDisabled = true;
            completeTrade.Quote.PurchaseIdentities = new[] { completeTrade.Containers[1].Items[2].Identity };
            var selectedTrade = completeTradeProjection.Capture("trade-world", true, () => completeTrade);
            check("schema2_native_single_selection_excludes_other_nonowned_back_items",
                BusinessWire.Validate(selectedTrade) && selectedTrade.details.quote.status == "complete" && selectedTrade.item_refs.Count == 1 &&
                selectedTrade.item_refs[0] == selectedTrade.details.containers[1].item_refs[2]);
            var forgedOwnedPurchase = new BusinessObservation
            {
                support = selectedTrade.support,
                customer_ref = selectedTrade.customer_ref,
                item_refs = new List<string>(selectedTrade.item_refs),
                offer_ref = selectedTrade.offer_ref,
                collection_fingerprint = selectedTrade.collection_fingerprint,
                details = BusinessWire.Clone(selectedTrade.details)
            };
            forgedOwnedPurchase.item_refs = new List<string> { forgedOwnedPurchase.details.containers[1].item_refs[1] };
            check("schema2_wire_rejects_owned_item_as_purchase_subset", !BusinessWire.Validate(forgedOwnedPurchase));

            collectionInput.Containers[1].Items[0].Units++;
            var collectionC = collectionProjection.Capture("w", true, () => collectionInput);
            check("business_unverified_collection_has_no_fingerprint_after_quantity_change", collectionC.collection_fingerprint == null);
            collectionInput.Containers[1].Items = new[] { collectionInput.Containers[1].Items[0],
                new BusinessItemInput { Identity = new Identity(), Units = 1, Owned = false } };
            var collectionD = collectionProjection.Capture("w", true, () => collectionInput);
            check("business_unverified_collection_has_no_fingerprint_after_membership_change", collectionD.collection_fingerprint == null);
            collectionInput.Containers[1].Completeness = "incomplete";
            collectionInput.Containers[1].Reason = "member_visibility_unverified";
            var collectionIncomplete = collectionProjection.Capture("w", true, () => collectionInput);
            check("business_incomplete_collection_has_no_fingerprint", collectionIncomplete.collection_fingerprint == null);

            var emptyFp = BusinessCollection.Fingerprint("w", "customer-1", "sell", Array.Empty<BusinessCollectionMember>(),
                "unknown", "unverified", null);
            check("business_empty_collection_has_no_fingerprint", emptyFp == null);
            check("business_total_parser_accepts_observed_chinese_currency_glyph", BusinessQuoteParsing.IsTotalLabel("总计") &&
                BusinessQuoteParsing.ParseVisibleTotal("285\uE000") == 285);
            check("business_total_parser_rejects_compound_or_malformed_visible_values", !BusinessQuoteParsing.IsTotalLabel("总计／批发价") &&
                BusinessQuoteParsing.ParseVisibleTotal("285\uE000 / 223\uE000 (-22%)") == null &&
                BusinessQuoteParsing.ParseVisibleTotal("12,34") == null);
            check("business_total_parser_separates_visible_normal_and_wholesale_amounts",
                BusinessQuoteParsing.ParseVisibleNormalTotal("285\uE000 / 223\uE000 (-22%)") == 285 &&
                BusinessQuoteParsing.ParseVisibleWholesaleTotal("285\uE000 / 223\uE000 (-22%)") == 223);

            var shaped = Sample();
            shaped.Containers[0].Items[0].ShapeStatus = "known";
            shaped.Containers[0].Items[0].Width = 2;
            shaped.Containers[0].Items[0].Height = 3;
            var shapedResult = new BusinessProjection().Capture("shape-world", true, () => shaped);
            check("business_known_shape_projects_only_positive_bounding_dimensions", shapedResult.details.items[0].shape_status == "known" &&
                shapedResult.details.items[0].width == 2 && shapedResult.details.items[0].height == 3);

            var throwingProjection = new BusinessProjection();
            var beforeThrow = throwingProjection.Capture("w", true, () => other);
            var failed = throwingProjection.Capture("w", true, () => throw new InvalidOperationException("do not leak"));
            var recovered = throwingProjection.Capture("w", true, () => other);
            check("business_exception_clears_refs_and_recovers", failed.details.status == "unavailable" && failed.customer_ref == null &&
                recovered.customer_ref != beforeThrow.customer_ref);
            check("business_exception_text_not_leaked", !JsonSerializer.Serialize(failed).Contains("do not leak", StringComparison.Ordinal));

            var bounded = Sample();
            bounded.Customer.Phase = new string('x', 300);
            var boundedResult = new BusinessProjection().Capture("w", true, () => bounded);
            check("business_labels_are_bounded", boundedResult.details.status == "unavailable");

            var oversized = Sample();
            oversized.Quote.Rows = new BusinessQuoteRowInput[40];
            for (int i = 0; i < oversized.Quote.Rows.Length; i++)
                oversized.Quote.Rows[i] = new BusinessQuoteRowInput { Name = new string('n', 256), PriceText = new string('p', 256) };
            var oversizedResult = new BusinessProjection().Capture("w", true, () => oversized);
            check("business_global_string_budget_fails_closed", oversizedResult.details.status == "unavailable" &&
                oversizedResult.details.containers[0].item_refs.Count == 0);

            var blocked = Sample();
            blocked.Ui.Status = "blocked";
            blocked.Ui.Blockers = new[] { "trade_confirmation" };
            var blockedResult = new BusinessProjection().Capture("w", true, () => blocked);
            check("business_known_blocker_hides_inventory_and_quote", blockedResult.details.items.Count == 0 &&
                blockedResult.item_refs.Count == 0 && blockedResult.details.containers[0].completeness == "incomplete" &&
                blockedResult.details.quote.status == "unavailable" && blockedResult.details.quote.displayed_total == null);

            check("business_wire_dto_has_strict_public_field_whitelist", Whitelisted(typeof(BusinessObservation), new HashSet<Type>()));

            var mutation = Sample();
            var copied = new BusinessProjection().Capture("w", true, () => mutation);
            string copiedName = copied.details.items[0].name;
            string copiedLabel = copied.details.quote.rows[0].name;
            mutation.Containers[0].Items[0].Name = "changed";
            mutation.Quote.Rows[0].Name = "changed quote";
            mutation.Ui.Blockers[0] = "changed blocker";
            check("business_output_detaches_input_values", copied.details.items[0].name == copiedName &&
                copied.details.quote.rows[0].name == copiedLabel && copied.details.ui.blockers[0] == "global_ui_stack_unobservable");
        }

        private static BusinessInput Sample()
        {
            return new BusinessInput
            {
                Customer = new BusinessCustomerInput { Identity = new Identity(), Presence = "present", Arrived = true,
                    TradeType = "sell", Phase = "trading", DialogueStage = "absent" },
                Containers = new[]
                {
                    new BusinessContainerInput { Container = "front", Completeness = "complete", Items = new[]
                    { new BusinessItemInput { Identity = new Identity(), Name = "Visible label", Units = 2, Owned = true } } },
                    new BusinessContainerInput { Container = "back", Completeness = "complete" },
                    new BusinessContainerInput { Container = "player", Completeness = "complete" }
                },
                Quote = new BusinessQuoteInput { Status = "unverified", Mode = "unknown", Reason = "quote_binding_unverified", DisplayedTotal = 1234,
                    Rows = new[] { new BusinessQuoteRowInput { Name = "Quote label", PriceText = "12", Amount = 12 } } },
                Ui = new BusinessUiInput { Status = "unverified", Coverage = "known_roots_only", Complete = false,
                    Blockers = new[] { "global_ui_stack_unobservable" } }
            };
        }

        private static BusinessInput CompleteTradeSample()
        {
            var input = Sample();
            input.Customer.MultiBuyDisabled = false;
            input.Containers[1].Items = new[]
            {
                new BusinessItemInput { Identity = new Identity(), Name = "Visible cup", Units = 1, Owned = false },
                new BusinessItemInput { Identity = new Identity(), Name = "Owned paper", Units = 1, Owned = true },
                new BusinessItemInput { Identity = new Identity(), Name = "Visible lamp", Units = 2, Owned = false }
            };
            input.Quote.Status = "complete";
            input.Quote.Mode = "normal";
            input.Quote.DisplayedTotal = 1234;
            input.Quote.WholesaleTotal = 900;
            input.Quote.Reason = null;
            input.Quote.BindingVerified = true;
            input.Quote.PurchaseIdentities = new[]
            {
                input.Containers[1].Items[0].Identity,
                input.Containers[1].Items[2].Identity
            };
            input.Quote.Rows = new[]
            {
                new BusinessQuoteRowInput { Name = "Visible cup", PriceText = "$500" },
                new BusinessQuoteRowInput { Name = "Visible lamp", PriceText = "$734" }
            };
            return input;
        }

        private static bool Whitelisted(Type type, HashSet<Type> seen)
        {
            if (type == null || !seen.Add(type)) return true;
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>))
                return Whitelisted(type.GetGenericArguments()[0], seen);
            if (type.IsArray) return Whitelisted(type.GetElementType(), seen);
            if (type.IsPrimitive || type == typeof(string) || type == typeof(int?) || type == typeof(bool?)) return true;
            if (type.Assembly != typeof(BusinessObservation).Assembly || !type.IsDefined(typeof(SerializableAttribute), false)) return false;
            foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.Public))
            {
                if (field.FieldType == typeof(object) || typeof(Delegate).IsAssignableFrom(field.FieldType) ||
                    field.Name.IndexOf("identity", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    field.Name.IndexOf("unique", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    !Whitelisted(field.FieldType, seen)) return false;
            }
            return true;
        }
    }
}
