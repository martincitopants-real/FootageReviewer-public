using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace FootageReviewer.App.Util;

/// <summary>
/// Conservative autocorrect: a curated map of common English misspellings → fix. Deliberately exact-match
/// only (no fuzzy/dictionary correction) so it can't mangle slang, game names, or usernames. Casing of the
/// original token is preserved (ALLCAPS / Title / lower). Callers apply it on a word boundary and offer a
/// one-keystroke undo.
/// </summary>
public static class AutoCorrect
{
    // Built with indexer assignment so any accidental duplicate keys overwrite rather than throw.
    private static readonly Dictionary<string, string> Map = Build();

    private static Dictionary<string, string> Build()
    {
        var m = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        void A(string k, string v) => m[k] = v;

        // contractions (only unambiguous ones; "its"/"id"/"ill"/"were" are valid words → excluded)
        A("dont", "don't"); A("doesnt", "doesn't"); A("didnt", "didn't"); A("cant", "can't");
        A("wont", "won't"); A("couldnt", "couldn't"); A("shouldnt", "shouldn't"); A("wouldnt", "wouldn't");
        A("isnt", "isn't"); A("wasnt", "wasn't"); A("werent", "weren't"); A("arent", "aren't");
        A("hasnt", "hasn't"); A("havent", "haven't"); A("hadnt", "hadn't"); A("youre", "you're");
        A("theyre", "they're"); A("theyve", "they've"); A("youve", "you've"); A("weve", "we've");
        A("thats", "that's"); A("whats", "what's"); A("heres", "here's"); A("theres", "there's");
        A("im", "I'm"); A("ive", "I've"); A("i", "I"); // "id"/"ill" left alone — real words

        // common misspellings
        A("teh", "the"); A("hte", "the"); A("adn", "and"); A("nad", "and"); A("taht", "that"); A("thsi", "this");
        A("tihs", "this"); A("wiht", "with"); A("wihtout", "without"); A("whtouht", "without");
        A("withing", "within"); A("wich", "which"); A("whcih", "which"); A("wihch", "which");
        A("recieve", "receive"); A("recieved", "received"); A("recieving", "receiving");
        A("beleive", "believe"); A("beleived", "believed"); A("belive", "believe");
        A("seperate", "separate"); A("seperated", "separated"); A("definately", "definitely");
        A("definatly", "definitely"); A("definate", "definite"); A("occured", "occurred");
        A("occuring", "occurring"); A("occurance", "occurrence"); A("untill", "until");
        A("tommorow", "tomorrow"); A("tomorow", "tomorrow"); A("thier", "their"); A("freind", "friend");
        A("freinds", "friends"); A("becuase", "because"); A("becasue", "because"); A("accross", "across");
        A("agressive", "aggressive"); A("apparant", "apparent"); A("aparent", "apparent");
        A("arguement", "argument"); A("calender", "calendar"); A("cemetary", "cemetery");
        A("collegue", "colleague"); A("comming", "coming"); A("commited", "committed");
        A("commitee", "committee"); A("concious", "conscious"); A("dissapoint", "disappoint");
        A("dissapointed", "disappointed"); A("embarass", "embarrass"); A("enviroment", "environment");
        A("enviroments", "environments"); A("existance", "existence"); A("familar", "familiar");
        A("finaly", "finally"); A("finnally", "finally"); A("foriegn", "foreign"); A("goverment", "government");
        A("gaurd", "guard"); A("happend", "happened"); A("happenned", "happened"); A("harrass", "harass");
        A("immediatly", "immediately"); A("imediately", "immediately"); A("independant", "independent");
        A("knowlege", "knowledge"); A("liason", "liaison"); A("libary", "library"); A("lisence", "license");
        A("maintainance", "maintenance"); A("neccessary", "necessary"); A("necessery", "necessary");
        A("occassion", "occasion"); A("ocasion", "occasion"); A("persistant", "persistent");
        A("posession", "possession"); A("prefered", "preferred"); A("priviledge", "privilege");
        A("probaly", "probably"); A("probly", "probably"); A("proabbly", "probably");
        A("pronounciation", "pronunciation"); A("publically", "publicly"); A("recomend", "recommend");
        A("recomended", "recommended"); A("refered", "referred"); A("relevent", "relevant");
        A("irrelevent", "irrelevant"); A("religous", "religious"); A("succesful", "successful");
        A("successfull", "successful"); A("sucessful", "successful"); A("unsuccesful", "unsuccessful");
        A("sucessfully", "successfully"); A("suprise", "surprise"); A("suprised", "surprised");
        A("threshhold", "threshold"); A("truely", "truly"); A("unfortunatly", "unfortunately");
        A("unfortuntely", "unfortunately"); A("usualy", "usually"); A("vaccuum", "vacuum");
        A("wierd", "weird"); A("writting", "writing"); A("yeild", "yield"); A("becomming", "becoming");
        A("begining", "beginning"); A("bizzare", "bizarre"); A("buisness", "business");
        A("calculater", "calculator"); A("carrer", "career"); A("catagory", "category");
        A("cieling", "ceiling"); A("completly", "completely"); A("completey", "completely");
        A("desicion", "decision"); A("alright", "all right"); A("alot", "a lot"); A("aswell", "as well");
        A("infront", "in front"); A("everytime", "every time"); A("eachother", "each other");
        A("abit", "a bit"); A("alittle", "a little"); A("incase", "in case"); A("upto", "up to");
        A("thankyou", "thank you"); A("greatful", "grateful"); A("grammer", "grammar"); A("hieght", "height");
        A("heigth", "height"); A("lenght", "length"); A("lengh", "length"); A("strenght", "strength");
        A("wieght", "weight"); A("mispell", "misspell"); A("noticable", "noticeable"); A("paralel", "parallel");
        A("payed", "paid"); A("peice", "piece"); A("persue", "pursue"); A("posible", "possible");
        A("potatos", "potatoes"); A("preformance", "performance"); A("quater", "quarter"); A("realy", "really");
        A("rember", "remember"); A("remeber", "remember"); A("rythm", "rhythm"); A("rythym", "rhythm");
        A("secratary", "secretary"); A("sieze", "seize"); A("similer", "similar"); A("sincerley", "sincerely");
        A("speach", "speech"); A("tendancy", "tendency");
        A("twelth", "twelfth"); A("wether", "whether"); A("wheter", "whether"); A("yatch", "yacht");
        // (informal "tho"/"thru" deliberately NOT corrected — common intentional spellings in logs)
        A("acomplish", "accomplish"); A("acheive", "achieve"); A("acheived", "achieved");
        A("accomodate", "accommodate"); A("adress", "address"); A("agian", "again"); A("allready", "already");
        A("anual", "annual"); A("arround", "around"); A("athiest", "atheist"); A("awknowledge", "acknowledge");
        A("basicly", "basically"); A("benifit", "benefit"); A("betwen", "between"); A("carefull", "careful");
        A("choosen", "chosen"); A("collumn", "column"); A("comparsion", "comparison"); A("controll", "control");
        A("controled", "controlled"); A("conviced", "convinced"); A("critized", "criticized");
        A("curiousity", "curiosity"); A("dammage", "damage"); A("effecient", "efficient");
        A("eligable", "eligible"); A("especialy", "especially"); A("excelent", "excellent");
        A("exibit", "exhibit"); A("expirience", "experience"); A("facinating", "fascinating");
        A("flexable", "flexible"); A("fourty", "forty"); A("fullfil", "fulfil"); A("futher", "further");
        A("garentee", "guarantee"); A("glich", "glitch"); A("heirarchy", "hierarchy");
        A("humourous", "humorous"); A("hygene", "hygiene"); A("idae", "idea"); A("ilness", "illness");
        A("inteligence", "intelligence"); A("interupt", "interrupt"); A("jist", "gist");
        A("lazyness", "laziness"); A("levle", "level"); A("liek", "like"); A("lieing", "lying");
        A("loosing", "losing"); A("mabye", "maybe"); A("meausre", "measure"); A("medecine", "medicine");
        A("millon", "million"); A("mischevious", "mischievous"); A("mounth", "month"); A("neice", "niece");
        A("nieghbor", "neighbor"); A("offical", "official"); A("ommit", "omit"); A("oponent", "opponent");
        A("oportunity", "opportunity"); A("orginal", "original"); A("paticular", "particular");
        A("peolpe", "people"); A("peopel", "people"); A("perhasp", "perhaps"); A("prehaps", "perhaps");
        A("perminent", "permanent"); A("personaly", "personally"); A("postion", "position");
        A("poshion", "position"); A("practiclly", "practically"); A("presance", "presence");
        A("proffesional", "professional"); A("profesional", "professional"); A("promiss", "promise");
        A("puting", "putting"); A("questionaire", "questionnaire"); A("reconize", "recognize");
        A("recquire", "require"); A("responsibilty", "responsibility"); A("resturant", "restaurant");
        A("ridiculus", "ridiculous"); A("safty", "safety"); A("satelite", "satellite"); A("scedule", "schedule");
        A("shedule", "schedule"); A("seing", "seeing"); A("sence", "sense"); A("sentance", "sentence");
        A("shoudl", "should"); A("smae", "same"); A("somthing", "something"); A("soemthing", "something");
        A("speciffic", "specific"); A("stoping", "stopping"); A("sumary", "summary");
        A("supicious", "suspicious"); A("surley", "surely"); A("themself", "themselves");
        A("togehter", "together"); A("togather", "together"); A("tounge", "tongue"); A("towrds", "towards");
        A("uneccessary", "unnecessary"); A("unforseen", "unforeseen"); A("usefull", "useful");
        A("vehical", "vehicle"); A("visable", "visible"); A("watn", "want"); A("whant", "want");
        A("wensday", "Wednesday"); A("worht", "worth"); A("yoru", "your"); A("yuor", "your"); A("yor", "your");
        A("thn", "then"); A("tehn", "then"); A("shoud", "should"); A("woudl", "would"); A("coudl", "could");
        A("alwasy", "always"); A("alway", "always"); A("becuse", "because"); A("diffrent", "different");
        A("diffrence", "difference"); A("excpet", "except"); A("explaning", "explaining");
        A("gonig", "going"); A("jsut", "just"); A("knwo", "know"); A("knpw", "know"); A("liiked", "liked");
        A("muhc", "much"); A("nver", "never"); A("ohter", "other"); A("onece", "once"); A("onyl", "only");
        A("ot", "to"); A("pleae", "please"); A("plesae", "please"); A("realyl", "really");
        A("shouldve", "should've"); A("couldve", "could've"); A("wouldve", "would've"); A("somehing", "something");
        A("succed", "succeed"); A("waht", "what"); A("wnat", "want"); A("wokr", "work");
        // more common typos (M18)
        A("abuot", "about"); A("acutally", "actually"); A("actualy", "actually"); A("almsot", "almost");
        A("alomst", "almost"); A("anwser", "answer"); A("anwsers", "answers"); A("appropiate", "appropriate");
        A("aroudn", "around"); A("bcak", "back"); A("ahve", "have"); A("haev", "have");
        A("charcter", "character"); A("charecter", "character"); A("comapny", "company");
        A("differnt", "different"); A("enought", "enough"); A("enoguh", "enough"); A("everthing", "everything");
        A("evrything", "everything"); A("everyting", "everything"); A("excercise", "exercise");
        A("experiance", "experience"); A("feild", "field"); A("finsih", "finish"); A("finsihed", "finished");
        A("follwing", "following"); A("folowing", "following"); A("foward", "forward"); A("genuis", "genius");
        A("herad", "heard"); A("importnat", "important"); A("improtant", "important"); A("intresting", "interesting");
        A("intersting", "interesting"); A("littel", "little"); A("makign", "making"); A("managment", "management");
        A("mesage", "message"); A("messge", "message"); A("necesary", "necessary"); A("ocurred", "occurred");
        A("offen", "often"); A("oftern", "often"); A("perfomance", "performance"); A("porblem", "problem");
        A("problme", "problem"); A("probelm", "problem"); A("queston", "question"); A("questoin", "question");
        A("reccomend", "recommend"); A("responce", "response"); A("similiar", "similar"); A("somethign", "something");
        A("tahn", "than"); A("tehy", "they"); A("thye", "they"); A("themselfs", "themselves"); A("theese", "these");
        A("thign", "thing"); A("thigns", "things"); A("tihng", "thing"); A("throuhg", "through");
        A("tommorrow", "tomorrow"); A("tonihgt", "tonight"); A("udnerstand", "understand");
        A("understnad", "understand"); A("undersand", "understand"); A("unitl", "until"); A("verison", "version");
        A("verson", "version"); A("vrey", "very"); A("veyr", "very"); A("wonderfull", "wonderful");
        A("wroking", "working"); A("workign", "working"); A("yera", "year"); A("yuors", "yours");
        // more general typos (M21)
        A("alos", "also"); A("aslo", "also"); A("nto", "not"); A("becouse", "because"); A("beacuse", "because");
        A("definetly", "definitely"); A("defenitely", "definitely"); A("occassionally", "occasionally");
        A("seperatly", "separately"); A("accomodation", "accommodation"); A("neccesary", "necessary");
        A("whaht", "what"); A("tehre", "there"); A("thre", "there"); A("fitler", "filter"); A("fitlers", "filters");
        A("computor", "computer"); A("cmoputer", "computer"); A("tiem", "time"); A("tiems", "times");
        A("frist", "first"); A("fisrt", "first"); A("fianlly", "finally"); A("soudn", "sound"); A("soudns", "sounds");
        A("owrk", "work"); A("owrking", "working"); A("thgat", "that"); A("taht", "that"); A("usaully", "usually");
        A("actaully", "actually"); A("agian", "again"); A("aywas", "always"); A("clikc", "click"); A("clikced", "clicked");
        A("scroup", "scroll"); A("relaly", "really"); A("relly", "really"); A("witth", "with"); A("abotu", "about");
        A("whcih", "which"); A("wnated", "wanted"); A("smoe", "some"); A("liek", "like"); A("liekd", "liked");
        return m;
    }

    // Multi-word phrase fixes (run AFTER single-token correction, since these span a space the tokenizer splits
    // on). First-letter case is preserved. Conservative list only — no ambiguous merges.
    private static readonly (Regex Rx, string Fix)[] PhraseRules = BuildPhrases();
    private static (Regex, string)[] BuildPhrases()
    {
        var phrases = new (string Phrase, string Fix)[]
        {
            ("int he", "in the"), ("in stead", "instead"),
            ("would of", "would have"), ("could of", "could have"), ("should of", "should have"),
            ("must of", "must have"), ("might of", "might have"),
        };
        return phrases.Select(p =>
            (new Regex($@"\b{Regex.Escape(p.Phrase)}\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), p.Fix)).ToArray();
    }

    /// <summary>Apply the multi-word phrase fixes, preserving the matched phrase's leading-letter case.</summary>
    public static string ApplyPhrases(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        foreach (var (rx, fix) in PhraseRules)
            text = rx.Replace(text, m =>
                m.Value.Length > 0 && char.IsUpper(m.Value[0]) ? char.ToUpperInvariant(fix[0]) + fix[1..] : fix);
        return text;
    }

    /// <summary>True if <paramref name="token"/> has a curated correction. Outputs it case-matched.</summary>
    public static bool TryCorrect(string token, out string fix)
    {
        fix = token;
        if (string.IsNullOrEmpty(token)) return false;
        // Protect things that aren't ordinary words: usernames/tags and tokens containing digits.
        if (token[0] == '@' || token.Any(char.IsDigit)) return false;
        if (!Map.TryGetValue(token, out var canon)) return false;
        var cased = MatchCase(token, canon);
        if (string.Equals(cased, token, StringComparison.Ordinal)) return false; // no-op (already correct)
        fix = cased;
        return true;
    }

    /// <summary>Apply autocorrect to every whitespace/punctuation-delimited token in <paramref name="text"/>.</summary>
    public static string CorrectAll(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        var sb = new StringBuilder(text.Length + 8);
        int i = 0;
        while (i < text.Length)
        {
            if (!IsWordChar(text[i])) { sb.Append(text[i]); i++; continue; }
            int start = i;
            while (i < text.Length && IsWordChar(text[i])) i++;
            var token = text.Substring(start, i - start);
            sb.Append(TryCorrect(token, out var fix) ? fix : token);
        }
        return ApplyPhrases(sb.ToString()); // then fix multi-word phrases ("int he" → "in the", etc.)
    }

    public static bool IsWordChar(char c) => char.IsLetter(c) || c == '\'';

    /// <summary>True if the word starting at <paramref name="start"/> begins a sentence — nothing but
    /// whitespace precedes it, or the last non-space char before it is . ! or ?.</summary>
    public static bool IsSentenceStart(string text, int start)
    {
        var i = start - 1;
        while (i >= 0 && char.IsWhiteSpace(text[i])) i--;
        if (i < 0) return true;
        var c = text[i];
        return c == '.' || c == '!' || c == '?';
    }

    /// <summary>Capitalize the first letter of each sentence (used on submit so the logged text is clean
    /// even when the user never typed a trailing space after the last word).</summary>
    public static string CapitalizeSentences(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        var a = text.ToCharArray();
        var startOfSentence = true;
        for (var i = 0; i < a.Length; i++)
        {
            var c = a[i];
            if (char.IsWhiteSpace(c)) continue;
            if (startOfSentence && char.IsLetter(c)) a[i] = char.ToUpperInvariant(c);
            startOfSentence = c is '.' or '!' or '?';
        }
        return new string(a);
    }

    /// <summary>Re-apply the source token's casing to the canonical fix.</summary>
    public static string MatchCase(string src, string canon)
    {
        if (string.IsNullOrEmpty(src) || string.IsNullOrEmpty(canon)) return canon;
        bool allUpper = src.Length > 1 && src.All(c => !char.IsLetter(c) || char.IsUpper(c));
        if (allUpper) return canon.ToUpperInvariant();
        if (char.IsUpper(src[0])) return char.ToUpperInvariant(canon[0]) + canon.Substring(1);
        return canon;
    }
}
