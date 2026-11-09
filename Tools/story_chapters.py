# Generates Tools/Scenarios/chapter_NN_*.json, the main-story walk (Docs/story-walk.md).
# Edit here and re-run `python Tools/story_chapters.py`; the JSON files are what runners execute.
# Check offline afterwards: `dotnet run --project Tools/G3Cli -- story walk --dry all`.
import json, os

OUT = os.path.join(os.path.dirname(os.path.abspath(__file__)), 'Scenarios')
HERO = "god; notips; safe; time 12; level 25; attr STR 250; attr DEX 150; equip It_1H_Sword_01"

# What a player has done by the END of each chapter (cumulative): chapter N starts from STATE[1..N-1].
STATE = {
    1: ["enclave Ardea liberate", "flag Diego_WarIsLost,Diego_FindXardas",
        "played BPANKRATZ31451,FILLER65,FILLER925,BPANKRATZ31453,BPANKRATZ31454,BPANKRATZ31455"],
    2: ["flag Gorn_RebsThere", "quest Gorn_ShowReddock Success", "played BPANKRATZ31458,BPANKRATZ31442,BPANKRATZ31445,BPANKRATZ31446,BPANKRATZ31447"],
    3: ["flag Roland_HisDuties,Roland_Paladin", "give It_Teleport_VangardTempel", "quest PC_Hero_FIndTeleportToKing Success",
        "played BPANKRATZ31024,BPANKRATZ31084,SKALVERAM3166"],
    4: ["quest Xardas_FindXardas Success", "flag Xardas_Plan", "quest PC_Hero_FindArtefacts Running", "played FILLER4,FILLER7"],
    5: ["quest Rhobar_XardasPlan Success", "flag Rhobar_Artefacts", "played FILLER37,FILLER53,FILLER108,SKALVERAM3168"],
    6: ["flag Player_guest,Ugrasal_OK,Ishtar_Test_Success,Zuben_Deal", "quest Ishtar_Test,Zuben_XardasPlan Success",
        "played FILLER50,FILLER410,FILLER565,FILLER645,FILLER113,SKALVERAM3177,SKALVERAM3194"],
    7: ["give It_Artefact_Amulet", "played BPANKRATZ3491,BPANKRATZ3492,BPANKRATZ3494,BPANKRATZ3496,BPANKRATZ3497,BPANKRATZ3498"],
    8: ["quest PC_Hero_FindRobe Running", "give It_Artefact_Robe", "give It_Artefact_Crown", "quest PC_Hero_FifthTemple Success",
        "quest PC_Hero_FindChumanas Running", "flag Dargoth_Chronicles", "played FILLER413,SKALVERAM3322,SKALVERAM3331"],
    9: ["give It_Artefact_Ring_01", "give It_Artefact_Ring_02", "expect quest:PC_Hero_FindArtefacts=Success"],
    10: [],
    11: ["give It_Staff", "give It_Scepter", "give It_Fokus", "quest Innos_Contact,Beliar_Contact,Adanos_DestroyArtefacts Running",
         "played FILLER12,FILLER13,FILLER26,FILLER454,FILLER275,FILLER272"],
}


def setup(n):
    lines = ["log -- setup: the state after chapter %02d" % (n - 1)] if n > 1 else []
    for i in range(1, n):
        lines += STATE.get(i, [])
    return lines


def chapter(n, name, verifies, steps, notes, hero=HERO, timeout=900):
    spec = {"name": "chapter_%02d_%s" % (n, name), "verifies": verifies, "fresh": True, "timeout": timeout, "notes": notes,
            "steps": [hero] + setup(n) + ["log -- chapter %02d" % n] + steps}
    with open(os.path.join(OUT, spec["name"] + ".json"), "w", encoding="utf-8", newline="\n") as f:
        json.dump(spec, f, ensure_ascii=False, indent=2)
        f.write("\n")


def kill(npc, tp="dist=3"):
    """The hero's real melee until the NPC is down, then the finisher (Kill quests count deaths only)."""
    return ["tp npc:%s %s" % (npc, tp), "calm 40 except=%s" % npc, "fight %s hp=5 secs=30" % npc, "finisher %s" % npc,
            "expect state:%s=Dead" % npc, "uncalm", "safe"]


chapter(1, "ardea",
        "story-walk ch1: New Game in Ardea; the orcs are beaten (Ardea_Revolution); the four friends; Diego sends the hero to find Xardas",
        ["expect quest:Xardas_FindXardas=Running", "expect chapter=1", "expect present:Diego", "expect enclave:Ardea!=Flee",
         "shot ch01_start",
         "log -- the orcs of Ardea (kill path: alarm, liberation check)", "raid Ardea",
         "wait until=enclave:Ardea=Flee timeout=20", "wait until=quest:Ardea_Revolution=Success timeout=10", "safe",
         "log -- the friends", "tp npc:Lester dist=2", "bring Lester near=place:Lester_Start dist=0.5", "tp npc:Lester dist=2",
         "ask Lester BPANKRATZ31451", "expect flag:Gorn_SentDiego",
         "tp npc:Hamlar dist=2", "ask Hamlar FILLER65", "expect given:FILLER65",
         "tp place:Ardea_4Friends", "bring Diego near=place:Ardea_4Friends dist=0.5", "tp npc:Diego dist=2",
         "ask Diego BPANKRATZ31454", "expect flag:Diego_WarIsLost", "ask Diego BPANKRATZ31455", "expect flag:Diego_FindXardas",
         "expect !flag:Gorn_SentDiego", "expect quest:Xardas_FindXardas=Running", "shot ch01_end look=npc:Diego"],
        "Real New Game state (no 'safe' before the raid: the orcs must stay hostile). Lester's Important needs him at Lester_Start, "
        "Diego's entries need him within 5 m of Ardea_4Friends.",
        hero="god; notips; time 12; level 10; attr STR 150; equip It_1H_Sword_01")

chapter(2, "reddock", "story-walk ch2: Gorn shows the way to the rebels' Reddock (FollowNpc Gorn_ShowReddock)",
        ["tp place:Ardea_4Friends", "bring Diego near=place:Ardea_4Friends dist=0.5", "tp npc:Diego dist=2",
         "ask Diego BPANKRATZ31458", "expect given:BPANKRATZ31458",
         "bring Gorn near=place:Ardea_4Friends dist=0.5", "tp npc:Gorn dist=2",
         "ask Gorn BPANKRATZ31445", "expect flag:Gorn_RebsThere",
         "ask Gorn BPANKRATZ31446", "expect quest:Gorn_ShowReddock=Running", "expect guide=Gorn",
         "log -- Gorn leads to Reddock (walk shortened: both teleported to FP_Reddock)",
         "tp place:FP_Reddock", "bring Gorn near=place:FP_Reddock dist=1",
         "ask Gorn BPANKRATZ31447", "expect quest:Gorn_ShowReddock=Success", "expect guide=-",
         "shot ch02_reddock look=npc:Gorn"],
        "BPANKRATZ31446 also needs Brenton, Gelford and Javier alive and not yet talked to. In play Gorn walks ahead; here both are "
        "teleported (the walk itself is the runner's free-roam check).")

chapter(3, "roland_teleport", "story-walk ch3: Roland (Okara) sends the hero for the teleport stone to the king; the stone lies near Gotha",
        ["tp npc:Roland dist=2", "ask Roland BPANKRATZ31024", "expect flag:Roland_HisDuties",
         "ask Roland BPANKRATZ31084", "expect flag:Roland_Paladin",
         "ask Roland SKALVERAM3166", "expect quest:PC_Hero_FIndTeleportToKing=Running",
         "log -- the teleport stone (world item near Gotha)", "tp 317.8,66.3,545.8", "calm 40",
         "pickup It_Teleport_VangardTempel radius=20", "expect has:It_Teleport_VangardTempel=1",
         "wait until=quest:PC_Hero_FIndTeleportToKing=Success timeout=5", "uncalm", "shot ch03_stone"],
        "Roland is in sector G3_Myrtana_01_Okara_NPC_01 (gone once Okara is liberated). The stone's world item is at "
        "(317.8,66.3,545.8), next to the Gotha demon.")

chapter(4, "xardas", "story-walk ch4: Xardas' tower in Nordmar (EnterArea Xardas_FindXardas); his plan: find the five artefacts",
        ["tp zone:Xardas_Tower", "wait until=quest:Xardas_FindXardas=Success timeout=10",
         "tp npc:Xardas dist=2", "ask Xardas FILLER7", "expect given:FILLER4", "expect flag:Xardas_Plan",
         "expect quest:PC_Hero_FindArtefacts=Running",
         "shot ch04_xardas look=npc:Xardas"],
        "EnterArea is counted once a second from the hero's reported pose (QuestAreaCounter). FILLER4 is a News entry that plays "
        "when the talk starts; FILLER7 is its sub-choice. Staff and scepter quests (NEWM0055/56) belong to the Adanos path.")

chapter(5, "rhobar", "story-walk ch5: Vengard: King Rhobar hears Xardas' plan (Rhobar_XardasPlan) and joins the artefact hunt",
        ["log -- the teleport stone would bring the hero to the Vengard temple, here tp to the king", "tp npc:Rhobar dist=2",
         "ask Rhobar FILLER53", "expect quest:Rhobar_XardasPlan=Running", "ask Rhobar SKALVERAM3168",
         "expect given:FILLER108", "expect quest:Rhobar_XardasPlan=Success", "expect flag:Rhobar_Artefacts",
         "shot ch05_rhobar look=npc:Rhobar"],
        "FILLER53 is a sub-choice of the News FILLER37, SKALVERAM3168 of FILLER108 (needs Xardas_Plan, chapter 4): ask chooses the parent first.")

chapter(6, "zuben_ishtar",
        "story-walk ch6: Ishtar (Varant): Zuben's test with Ugrasal (Ishtar_Test), then Zuben hears Xardas' plan (Zuben_XardasPlan, Zuben_Deal)",
        ["log -- setup: Ishtar's trust (enclave fame from the Ishtar side quests, Ugrasal's pre-check needs 75)",
         "fame Ishtar 80 kind=Enclave",
         "tp npc:Zuben dist=2", "ask Zuben FILLER410", "expect flag:Player_guest", "expect quest:Ishtar_Test=Running",
         "tp npc:Ugrasal dist=2", "ask Ugrasal FILLER565", "expect flag:Ishtar_Test_Running", "expect sector:Surus_Killer",
         "ask Ugrasal FILLER645", "expect flag:Ugrasal_OK",
         "tp npc:Zuben dist=2", "ask Zuben FILLER113", "expect quest:Ishtar_Test=Success",
         "ask Zuben SKALVERAM3177", "expect quest:Zuben_XardasPlan=Running", "ask Zuben SKALVERAM3194",
         "expect flag:Zuben_Deal", "expect quest:Zuben_XardasPlan=Success", "shot ch06_zuben look=npc:Zuben"],
        "FILLER50 (News) sets Player_guest when the first talk starts. Ugrasal is in G3_Varant_01_Ishtar_NPC_01. In G3 the test is "
        "to kill Surus (sector Surus_Killer); the data lets Ugrasal accept it directly (FILLER645).")

chapter(7, "amulet_trelis", "story-walk ch7: Trelis: Khabir tells about the amulet, Vak the orc has it (PC_Hero_FindAmulet)",
        ["tp npc:Khabir dist=2", "ask Khabir BPANKRATZ3491", "expect flag:Khabir_WantsOrcsToPerform",
         "ask Khabir BPANKRATZ3492", "ask Khabir BPANKRATZ3494", "ask Khabir BPANKRATZ3496", "ask Khabir BPANKRATZ3497",
         "ask Khabir BPANKRATZ3498", "expect quest:PC_Hero_FindAmulet=Running",
         "log -- Vak: knock out, loot", "tp npc:Vak dist=1.5 side=front", "calm 40 except=Vak", "fight Vak hp=5 secs=30", "loot Vak",
         "expect has:It_Artefact_Amulet=1", "wait until=quest:PC_Hero_FindAmulet=Success timeout=5", "uncalm", "safe",
         "shot ch07_amulet"],
        "Khabir is in G3_Myrtana_01_Trelis_NPC_01: do this before Trelis is liberated. Vak's knock-out is a crime in Trelis; calm "
        "keeps the other orcs out of the fight.")

chapter(8, "robe_crown_varant",
        "story-walk ch8: Varant temples: Saturas' robe (PC_Hero_FindRobe) and the crown from the temple chests; Myxir's fifth temple",
        ["tp npc:Saturas dist=2", "ask Saturas FILLER413", "expect quest:PC_Hero_FindRobe=Running",
         "tp -463.1,5.1,-1637.9", "calm 40", "loot obj:G3_Object_Interact_Animated_ChestPod_02", "expect has:It_Artefact_Robe=1",
         "wait until=quest:PC_Hero_FindRobe=Success timeout=5",
         "tp -1036.2,-6.6,-1797.9", "calm 40", "loot obj:G3_Object_Interact_Animated_ChestPod_02", "expect has:It_Artefact_Crown=1",
         "wait until=quest:PC_Hero_FindCrown=Success timeout=5", "uncalm",
         "tp npc:Myxir dist=2", "ask Myxir SKALVERAM3322", "expect quest:PC_Hero_FifthTemple=Running",
         "shot ch08_temples",
         "log -- the fifth temple: Dargoth in the Nordmar monastery points to the chronicles, the library's bookstand (BS_Chronicles)",
         "tp npc:Dargoth dist=2", "ask Dargoth SKALVERAM3331", "expect flag:Dargoth_Chronicles",
         "tp obj:G3_Object_Interact_Bookstand_Chronicles_01", "use obj:G3_Object_Interact_Bookstand_Chronicles_01",
         "expect quest:PC_Hero_FifthTemple=Success", "expect quest:PC_Hero_FindChumanas=Running",
         "expect sector:G3_Nordmar_01_Monastery_Chronicles_Story_01", "shot ch08_chronicles"],
        "Both artefacts lie in unlocked ChestPod_02 chests (robe -463,5,-1638; crown -1036,-7,-1798); loot takes the chest nearest "
        "the hero. PC_Hero_FindCrown has no starter: counted while Open. PC_Hero_FifthTemple's Report target MonasteryBook_Tester "
        "and the News entry of MonasteryBooik_Tester have no world entity in G3 or CP 1.75: the chronicles bookstand succeeds the "
        "quest (script BS_Chronicles), as in the original.")

chapter(9, "rings",
        "story-walk ch9: the rings: Gotha's demon (Ring_01) and the living ancestor in Nordmar (Ring_02); all five artefacts -> PC_Hero_FindArtefacts",
        ["tp npc:Gotha_Demon_01 dist=4", "calm 40 except=Gotha_Demon_01", "fight Gotha_Demon_01 hp=5 secs=30", "loot Gotha_Demon_01",
         "expect has:It_Artefact_Ring_01=1", "wait until=quest:PC_Hero_FindRing_01=Success timeout=5", "uncalm",
         "tp npc:LivingAncestor_01 dist=4", "calm 40 except=LivingAncestor_01", "fight LivingAncestor_01 hp=5 secs=30",
         "loot LivingAncestor_01", "expect has:It_Artefact_Ring_02=1", "wait until=quest:PC_Hero_FindRing_02=Success timeout=5",
         "uncalm", "wait until=quest:PC_Hero_FindArtefacts=Success timeout=5", "shot ch09_rings"],
        "Both rings come only from the bosses' inventories (static check). Gotha_Demon_01 (326.6,85.1,524.4), LivingAncestor_01 "
        "(888.5,166.2,1686.6).")

chapter(10, "firemage_cups",
        "story-walk ch10: Nordmar monastery: Milten and the twelve fire mage cups, blessing at the holy fire (side branch of the main story)",
        ["log -- Ardea staged Milten to the monastery once the hero left (> 420 m)", "tp place:Milten_Monastery",
         "bring Milten near=place:Milten_Monastery dist=0.5", "tp npc:Milten dist=2",
         "ask Milten SKALVERAM3649", "ask Milten SKALVERAM3650", "ask Milten SKALVERAM3653", "ask Milten SKALVERAM3654",
         "ask Milten SKALVERAM3655", "expect flag:Milten_Monastery_Cups", "expect quest:PC_Hero_Bless_FiremageCups=Running",
         "expect has:It_FiremageCup=1",
         "log -- setup: the other eleven cups (collected all over Myrtana and Varant in play)", "give It_FiremageCup 11",
         "tp obj:HolyFire_Tester", "ask HolyFire_Tester SKALVERAM3657",
         "expect sector:G3_Nordmar_01_Monastery_FiremageCups_Blessed",
         "log -- the Delivery entry hands the cups to the fire (G3 OnDelivery), the HasItems quest counts them",
         "expect has:It_FiremageCup=0",
         "wait until=quest:PC_Hero_Bless_FiremageCups=Success timeout=3", "shot ch10_cups"],
        "PC_Hero_FindAllFiremageCups is started by many NPCs. The holy fire's Delivery entry SKALVERAM3657 takes the twelve "
        "cups (built-in G3 delivery) and the Bless quest succeeds.")

chapter(11, "return_artefacts",
        "story-walk ch11: back with the five artefacts: Xardas (Adanos way: the focus), Zuben (the staff, Beliar way), Rhobar (the scepter, Innos way)",
        ["expect quest:PC_Hero_FindArtefacts=Success",
         "tp npc:Xardas dist=2", "ask Xardas FILLER12 end=0",
         "log -- FILLER13 is FILLER12's only sub-choice: G3 plays a lone sub-choice at once (no Description line, title = first hero line)",
         "expect given:FILLER13", "abort", "expect has:It_Fokus=1", "expect quest:Adanos_DestroyArtefacts=Running",
         "tp npc:Zuben dist=2", "ask Zuben FILLER26", "expect has:It_Staff=1", "expect quest:PC_Hero_FindStaff=Success",
         "expect given:FILLER454", "expect quest:Beliar_Contact=Running",
         "tp npc:Rhobar dist=2", "ask Rhobar FILLER275", "expect has:It_Scepter=1", "expect given:FILLER272",
         "expect quest:Innos_Contact=Running",
         "log -- the choice is made at a shrine: chapter_12 (Innos), chapter_13 (Beliar), chapter_14 (Adanos)",
         "shot ch11_choice look=npc:Rhobar"],
        "FILLER272 (Rhobar) and FILLER454 (Zuben) have no menu title: G3 plays them right after their parent, so after this chapter "
        "Innos_Contact and Beliar_Contact both run. FILLER13 (Xardas) plays right after FILLER12 as well: G3 "
        "(OnInfoScriptFinished) runs any list of exactly one info without the menu.")

chapter(12, "innos",
        "story-walk ch12: the Innos ending: Rhobar's scepter, the Innos shrine, kill Zuben, free Myrtana's towns -> Epilog_Innos",
        ["tp obj:Innos_Tester", "ask Innos_Tester FILLER36", "expect flag:Innos_chosen", "expect sector:G3_Nordmar_01_Story_Innos_01",
         "expect quest:Innos_Contact=Success",
         "talk Innos_Tester", "lines", "abort", "expect given:FILLER33", "expect given:SKALVERAM3209", "expect has:It_1H_FlameSword_01=1",
         "expect quest:Innos_FreeMyrtana=Running", "expect quest:Innos_KillZuben=Running"]
        + kill("Zuben") + ["expect quest:Innos_KillZuben=Success",
                           "log -- the towns (Ardea was freed in chapter 1)", "raid Faring", "raid Geldern", "raid KapDun",
                           "raid Montera", "raid Silden", "raid Trelis",
                           "wait until=quest:Innos_FreeMyrtana=Success timeout=5",
                           "tp obj:Innos_Tester", "ask Innos_Tester SKALVERAM3210 until=flag:Epilog_Innos", "expect given:SKALVERAM3196",
                           "expect given:SKALVERAM3188", "expect flag:Epilog_Innos",
                           "log -- the epilogue would start in this talk (player News BPANKRATZ31831), chapter_15 plays it",
                           "shot ch12_innos"],
        "The scepter and Innos_Contact come from Rhobar (chapter 11). FILLER36 is FILLER34's sub-choice. The News entries SKALVERAM3209/FILLER55/FILLER33 play when the talk starts. Raids are the "
        "hero's kill path, not a forced liberation. Next: chapter_15_epilogue.")

chapter(13, "beliar",
        "story-walk ch13: the Beliar ending: Zuben's offer, the Beliar shrine, kill the orc leaders, Rhobar, Zuben and Xardas -> Epilog_Beliar",
        ["tp obj:Beliar_Tester", "ask Beliar_Tester FILLER43", "expect flag:Beliar_chosen", "expect quest:Beliar_Contact=Success",
         "expect sector:G3_Varant_01_Story_Beliar_01", "expect has:It_Artefact_Crown=0",
         "talk Beliar_Tester", "lines", "abort", "expect given:FILLER866", "expect quest:Beliar_KillOrcs=Running", "expect quest:Beliar_KillRhobar=Running",
         "expect quest:Innos_KillZuben=Running", "expect quest:Beliar_KillXardas=Running"]
        + sum((kill(n, "dist=1.5 side=front") for n in ["Uruk", "Kan", "Varek", "Grok", "Umbrak", "Vak"]), [])
        + ["wait until=quest:Beliar_KillOrcs=Success timeout=5"] + kill("Rhobar") + kill("Zuben") + kill("Xardas")
        + ["tp obj:Beliar_Tester", "ask Beliar_Tester SKALVERAM3199 until=flag:Epilog_Beliar", "expect flag:Beliar_Orcs_OK", "expect flag:Beliar_Xardas_OK",
           "expect flag:Epilog_Beliar", "shot ch13_beliar"],
        "The staff and Beliar_Contact come from Zuben (chapter 11). FILLER43 is FILLER41's sub-choice. FILLER24/42/865/866 are News at the shrine (one talk plays all). Next: chapter_15_epilogue "
        "with Epilog_Beliar.")

chapter(14, "adanos",
        "story-walk ch14: the Adanos (Xardas) ending: destroy the artefacts in the ore furnace, kill Zuben and Rhobar, staff + scepter, "
        "Xardas to Adanos' gate -> Epilog_Adanos",
        ["expect has:It_Fokus=1", "expect quest:Adanos_DestroyArtefacts=Running",
         "tp obj:Erzofen", "ask Erzofen FILLER11", "expect flag:Burn_Ofen", "ask Erzofen FILLER31", "expect flag:Adanos_chosen",
         "expect has:It_Artefact_Crown=0", "wait until=quest:Adanos_DestroyArtefacts=Success timeout=5",
         "tp npc:Xardas dist=2", "ask Xardas FILLER28", "expect flag:Destroy_Success", "ask Xardas FILLER953",
         "expect flag:Xardas_need", "ask Xardas FILLER957", "expect quest:Innos_KillZuben=Running",
         "expect quest:Beliar_KillRhobar=Running"]
        + kill("Zuben") + kill("Rhobar")
        + ["loot Rhobar", "expect has:It_Scepter>=1", "wait until=quest:PC_Hero_FindScepter=Success timeout=5",
           "log -- FindStaff/FindScepter have no starter here (NEWM0055 needs FindStaff Open): both counted while Open",
           "tp npc:Xardas dist=2", "ask Xardas SKALVERAM3203", "ask Xardas SKALVERAM3204", "ask Xardas SKALVERAM3205",
           "ask Xardas SKALVERAM3206", "expect flag:Xardas_KingDead", "expect flag:Xardas_ZubenDead",
           "ask Xardas SKALVERAM3176", "expect flag:Xardas_Readytogo",
           "ask Xardas FILLER40", "expect quest:Xardas_GotoGate=Running", "expect guide=Xardas", "expect sector:Story_Xardas",
           "log -- Xardas leads to Adanos' gate (walk shortened)", "tp place:Adanos_Gate",
           "bring Xardas near=place:Adanos_Gate dist=1", "ask Xardas FILLER16", "expect quest:Xardas_GotoGate=Success",
           "tp obj:Adanos_Gate", "ask Adanos_Gate FILLER15", "expect quest:Adanos_OpenGate=Running",
           "ask Adanos_Gate FILLER35 until=flag:Epilog_Adanos", "expect quest:Adanos_OpenGate=Success", "expect flag:Epilog_Adanos",
           "expect flag:Endgame_XardasAlive", "shot ch14_adanos"],
        "Rhobar and Zuben attack once the artefacts are destroyed (their News SKALVERAM3211/3216). Known gap: the 'Xardas dead' variant "
        "(SKALVERAM3174) needs flag Xardas_DiesesEndeIstUeberfluessig, which nothing sets. Next: chapter_15_epilogue with Epilog_Adanos.")

chapter(15, "epilogue",
        "story-walk ch15: the epilogue slideshow and EndGame (Innos variant; set Epilog_Beliar or Epilog_Adanos instead for the others)",
        ["log -- ending flags as set by chapter 12 (Innos_Tester SKALVERAM3210)", "flag Epilog_Innos,Epilog_FirstInfo",
         "tp npc:Gorn dist=2", "talk Gorn", "wait until=flag:Epilog_01 timeout=10", "shot ch15_epilog_slide",
         "log -- the slides run as News entries, the last one (BPANKRATZ31835) ends the game: Play stops, no DONE line",
         "lines max=400"],
        "Runner: after 'lines' Play mode stops (EndGame -> credits -> quit). Record whether the slides showed (shot), the credits "
        "played and Play stopped without exceptions.", timeout=600)
print("ok")
