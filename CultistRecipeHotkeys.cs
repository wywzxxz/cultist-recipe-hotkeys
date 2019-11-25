using System;
using System.Collections.Generic;
using System.Linq;
using Assets.Core.Entities;
using Assets.Core.Interfaces;
using Assets.CS.TabletopUI;
using Assets.TabletopUi;
using TabletopUi.Scripts.Interfaces;
using UnityEngine;

namespace CultistRecipeHotkeys
{
    [BepInEx.BepInPlugin("net.robophreddev.CultistSimulator.CultistRecipeHotkeys", "CultistRecipeHotkeys", "0.0.1")]
    public class CultistRecipeHotkeysMod : BepInEx.BaseUnityPlugin
    {
        Dictionary<string, RecipeConfig> Recipes = new Dictionary<string, RecipeConfig>();

        private TabletopTokenContainer TabletopTokenContainer
        {
            get
            {
                {
                    var tabletopManager = (TabletopManager)Registry.Retrieve<ITabletopManager>();
                    if (tabletopManager == null)
                    {
                        this.Logger.LogError("Could not fetch TabletopManager");
                    }

                    return tabletopManager._tabletop;
                }
            }
        }

        void Start()
        {
            this.Logger.LogInfo("CultistHotbar initialized.");
        }
        long startTime = DateTime.Now.Ticks;   
        void Update()
        {
            //检测操作相关的操作
            try
            {
                var situation = this.GetOpenSituation();//当没打开situation时这个函数会报错
                if (!TabletopManager.IsInMansus() && situation != null)
                {
                    var Situation = situation.GetTokenId();
                    if (Input.GetKeyDown(KeyCode.F1) && situation.SituationClock.State == SituationState.Unstarted) //保存配方
                    {
                        if (!Recipes.ContainsKey(Situation)) Recipes[Situation] = new RecipeConfig();
                        this.StoreRecipe(Recipes[Situation]);
                        this.Notify("automation", "Recipe added");
                    }
                    if (Input.GetKeyDown(KeyCode.F2))//取消配方
                    {
                        Recipes.Remove(Situation);
                        SoundManager.PlaySfx("CardDragFail");
                        this.Notify("automation", "Recipe canceled");
                    }
                    if (Input.GetKeyDown(KeyCode.F3)) //调试用：立即执行该配方
                    {
                        this.Logger.LogError("F3");
                        this.RestoreRecipe(Recipes[Situation], true);
                    }
                }
            }catch(Exception e)
            {
            }
            //自动执行
            if (DateTime.Now.Ticks -this.startTime> 10000*100)
            {
                startTime = DateTime.Now.Ticks;                
                foreach (var item in Recipes.ToList())
                {

                    this.RestoreRecipe(item.Value, true);
                }
                startTime = DateTime.Now.Ticks;
            }
        }

        void StoreRecipe(RecipeConfig recipe)
        {            
            if (TabletopManager.IsInMansus())
            {
                return;
            }
            var situation = this.GetOpenSituation();
            if (situation == null)
            {
                return;
            }
            var slots = situation.situationWindow.GetStartingSlots();
            var elements = slots.Select(x => ValidRecipeSlotOrNull(x)).Select(x => x?.GetElementStackInSlot()?.EntityId);

            recipe.Situation = situation.GetTokenId();
            recipe.RecipeElements = elements.ToArray();                  
        }

        void RestoreRecipe(RecipeConfig recipe, bool executeOnRestore)
        {            
            var situation = this.GetSituation(recipe.Situation);
            if (situation == null)
            {
                return;
            }            
            this.Logger.LogError("  " + situation.SituationClock.State);
            switch (situation.SituationClock.State)
            {
                //case SituationState.RequiringExecution:
                case SituationState.Complete:
                    situation.situationWindow.DumpAllResultingCardsToDesktop();
                    break;
                case SituationState.Unstarted:
                    situation.situationWindow.DumpAllStartingCardsToDesktop();
                    break;
                default:
                    //SoundManager.PlaySfx("CardDragFail");
                    //this.Notify("I am busy", "I cannot start a recipe while I am busy doing somthing else.");
                    return;
            }

            // The first slot is the primary slot, so slot it independently.
            //  A successful slot here may cause new slots to be added.
            var primaryElement = recipe.RecipeElements.FirstOrDefault();
            //if (primaryElement == null) return;//针对无卡
            if (primaryElement != null)
            {
                var slot = situation.situationWindow.GetStartingSlots().FirstOrDefault();
                if (!slot || !this.TryPopulateSlot(slot, primaryElement))
                {
                    //this.Notify("Something is missing", "I cannot start this recipe, as I am missing a critical component.");
                    return;
                }
            }

            // Slot the remainder of the elements, now that
            //  the primary has opened up new slots for us.
            var slots = situation.situationWindow.GetStartingSlots();
            for (var i = 1; i < Math.Min(slots.Count, recipe.RecipeElements.Length); i++)
            {
                var element = recipe.RecipeElements[i];
                var slot = slots[i];
                this.TryPopulateSlot(slot, element);
            }

            if (executeOnRestore)
            {
                situation.AttemptActivateRecipe();
                if (situation.SituationClock.State == SituationState.Unstarted)
                {
                    this.Notify("Something went wrong", "I could not start the recipe.");
                    this.Logger.LogError("I could not start the recipe.");
                    situation.OpenWindow();
                }

                // If we started the recipe, there is no need to open the window.
            }
            else
            {
                situation.OpenWindow();
            }
        }

        bool TryPopulateSlot(RecipeSlot slot, string elementId)
        {
            if (slot.Defunct || slot.IsGreedy || slot.IsBeingAnimated)
            {
                return false;
            }

            var stack = this.GetStackForElement(elementId);
            if (stack == null)
            {
                return false;
            }

            this.PopulateSlot(slot, stack);
            return true;
        }

        void PopulateSlot(RecipeSlot slot, ElementStackToken stack)
        {
            stack.lastTablePos = new Vector2?(stack.RectTransform.anchoredPosition);
            if (stack.Quantity != 1)
            {
                var newStack = stack.SplitAllButNCardsToNewStack(stack.Quantity - 1, new Context(Context.ActionSource.PlayerDrag));
                slot.AcceptStack(newStack, new Context(Context.ActionSource.PlayerDrag));
            }
            else
            {
                slot.AcceptStack(stack, new Context(Context.ActionSource.PlayerDrag));
            }
        }

        ElementStackToken GetStackForElement(string elementId)
        {
            var tokens = this.TabletopTokenContainer.GetTokens();
            var elementStacks =
                from token in tokens
                let stack = token as ElementStackToken
                where stack != null && stack.EntityId == elementId
                select stack;
            return elementStacks.FirstOrDefault();
        }

        RecipeSlot ValidRecipeSlotOrNull(RecipeSlot slot)
        {
            if (slot.Defunct || slot.IsGreedy || slot.IsBeingAnimated)
            {
                return null;
            }
            return slot;
        }

        SituationController GetSituation(string entityId)
        {            
            var situation = Registry.Retrieve<SituationsCatalogue>().GetRegisteredSituations().FirstOrDefault(x => x.situationToken.EntityId == entityId);
            var token = situation.situationToken as SituationToken;
            if (token.Defunct || token.IsBeingAnimated)
            {
                return null;
            }

            return situation;
        }

        SituationController GetOpenSituation()
        {
            var sc = Registry.Retrieve<SituationsCatalogue>();
            if (sc == null) return null;
            var situation = sc.GetOpenSituation();
            var token = situation.situationToken as SituationToken;
            if (token.Defunct || token.IsBeingAnimated)
            {
                return null;
            }

            return situation;
        }

        void Notify(string title, string text)
        {
            Registry.Retrieve<INotifier>().ShowNotificationWindow(title, text);
        }
    }

    class RecipeConfig
    {
        public string Situation;
        public string[] RecipeElements;
    }
}