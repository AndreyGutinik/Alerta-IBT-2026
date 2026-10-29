using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using AlertaIBT.Core.Correction;

namespace AlertaIBT.Core.Tests.Correction;

// Batch 20: the owner saw ordinary words blocked, in the app and on the site (the site's alerts pass through this filter too).
// Every innocent word or phrase found by running the filter over the pt-BR dictionary and over realistic alert text is here
// and must pass; every explicit block and every disguise must still be blocked. Source kept ASCII-only (the filter folds
// accents, so "Cassio" and "C\U000000E1ssio" are the same text to it).
public partial class ProfanityFalsePositiveTests
{
    // Ordinary text the filter used to block, or that sits right next to a listed word. All of it must pass.
    public static readonly string[] Innocent =
    [
        // the bread, the colour, the horn, the animal, the form field, the breed, the writer and the cartoon
        "pao cacete", "pao cacete na padaria", "tom nude", "cor nude", "vestido nude", "bolsa nude", "esmalte nude",
        "corno de carneiro", "corno de uncao", "toque do corno", "tocar o corno", "cornos de carneiro",
        "carne de veado", "chifre de veado", "cacada de veados", 
        "sexo feminino", "sexo masculino", "reuniao do sexo feminino", "nome idade sexo", "ficha: nome, idade, sexo",
        "cavalo crioulo", "feijao crioulo", "crioulo haitiano", "galinha crioula", "lingua crioula",
        "Moby Dick", "Philip K. Dick", "Dick Vigarista",
        "Mongol Rally", "imperio mongol", "Grao-Mongol", "povo mongol",
        "por-nos de acordo", "p\U000000F4r-nos de acordo", "por nos de acordo",

        // surnames, names and places that carry a listed word or look like one
        "Pinto", "Pinto Silva", "Joao Pinto", "Pinto Coelho", "Rafael Pinto", "Pastor Pinto", "Diacono Pinto", "Irma Pinto",
        "Peixoto", "Rafael Peixoto", "Rafael", "Rafaela", "Cassio", "C\U000000E1ssio Pinto", "Cassia", "Pastor Cassio",
        "Analista", "Analista de sistemas", "Bucelas", "Putnam", "Putin", "Putti", "Pucci", "Pupo", "Xerez", "Xerox",
        "Cunha", "Rafael Cunha", "Curitiba", "Cuiaba", "Cuidado", "Cubatao", "Cupim", "Cuca", "Tia Cuca", "Cuscuz",
        "Paulo", "Seu Paulo", "Pauliceia", "Paula", "Pau-brasil", "Pica-pau", "Pica pau amarelo", "Sitio do Picapau Amarelo",
        "Tapioca", "Escaravelho", "Bostela", "Porrao", "Porao", "Corporacao", "Computador", "Computacao", "Deputado", "Deputada",
        "Controlada", "Disputa", "Reputacao", "Imputado", "Amputado", "Caracol", "Caraguatatuba", "Carapicuiba",
        "Pirassununga", "Piracicaba", "Pirenopolis", "Pirulito", "Pirata", "Marica", "Maricota", "Conar", "Conab",
        "Picanto", "Picasso", "Xsara Picasso", "Pikachu", "Piaui", "Pickup", "Picape", "Pirajui",
        "Shiitake", "Shirley", "Shittim", "madeira de Shittim", "Bastos", "Hancock", "Peacock", "Cockpit", "Cocktail", "Cocar", "Cocada",
        "Nigeria", "Niger", "Tarcisio", "Taranto", "Estupendo", "Estupefato", "Idioma", "Safra", "Safari", "Crente", "Crenca",
        "Escritorio", "Escritura", "Otavio", "Otaviano", "Baca", "Bacana", "Bacia", "Boicote", "Panela", "Panelada", "Viana", "Viaduto",
        "Bomba", "bomba de gasolina", "bomba de ar", "bomba d'agua", "Bombeiro", "Bombons",
        "Sexta", "Sexta-feira", "Sexto", "Sexagenario", "Unissex", "Tesouro", "Tesoureiro", "Tesoura", "Porrete",
        "Mongolia", "Cornelio Procopio", "Corneta", "Bichano", "Bicho", "Nudez", "Nuvem", "Fortaleza", "Fonda",
        "Chapada dos Veadeiros", "Rolandia", "Rola Moca", "pomba rola", "uma rola no telhado", "o que rola hoje",

        // alert sentences: car models, colours, plates and reasons
        "Gol branco ABC1D23 vidro aberto", "Fiat Uno preto ABC1D23 pneu furado", "Corsa prata ABC1234 bloqueando saida",
        "Celta vermelho ABC1D23 carro na cal\U000000E7ada", "Palio azul ABC1D23 porta aberta", "Onix preto ABC1D23 alarme tocando",
        "HB20 branco ABC1D23 luz acesa", "Kwid laranja ABC1D23 vidro aberto", "Pulse cinza ABC1D23 farol aceso",
        "Fusca azul ABC1D23 vidro aberto", "Kombi branca ABC1D23 pneu furado", "Strada vermelha ABC1D23 bloqueando o portao",
        "Hilux preta ABC1D23 alarme disparado", "S10 branca ABC1D23 farol aceso", "L200 preta ABC1D23 porta-malas aberto",
        "Picanto vermelho ABC1D23 vidro aberto", "Xsara Picasso prata ABC1D23 farol aceso", "Cerato preto PIC1A23 vidro aberto",
        "Honda Pop 110 azul ABC1D23 moto na cal\U000000E7ada", "Honda PCX 150 branca ABC1D23 farol aceso", "Yamaha Fazer 250 azul ABC1D23 farol",
        "Gol bola branco ABC1D23 pau de selfie no carro", "Saveiro prata ABC1D23 pau-brasil no porta-malas",
        "Fox prata ABC1D23 pica-pau no capo", "Fox prata CUR1T23 vidro quebrado", "Up azul XYZ9K87 arrombaram o carro",
        "Palio azul ABC1D23 carro arrombado", "Gol branco ABC1D23 vidro arrombado", "Uno prata ABC1D23 bomba",
        "Sala 0 Pinto", "Apto 4 Rola", "Gol 1 0 farol aceso", "Gol G5 branco ABC1D23 farol aceso", "Up! TSI prata ABC1D23 farol",
        "Citroen C3 branco ABC1D23 farol", "Citroen C4 Cactus branco ABC1D23 farol", "Mini Cooper branco ABC1D23 farol",
        "R$ 50,00 no bazar", "Culto de domingo 19h", "Reuniao dos obreiros", "Ensaio do coral na sala 3", "Santa Ceia hoje",
        "Cantina: coxinha, pastel, pao de queijo e cachorro-quente", "Pais do Pedro favor comparecer na recepcao",
    ];

    // What the owner asked to block, the disguises, and the words that keep their bare meaning of an insult.
    public static readonly string[] Profane =
    [
        // the owner's explicit blocks (batch 13)
        "seu pinto", "meu pau de selfie", "pi.ca", "minha pika", "o pinto", "a rola", "meu pau", "minha pica", "pica",
        "pi-ca", "pi_ca", "pi ca", "p i c a", "P I C A",
        // disguises: leetspeak, spaced letters, dots, a star, repeated letters, joined words, accents and capitals
        "k4ralho", "c4ralho", "caralhooo", "CARALHO", "seu p1nto", "meu p1nto", "p1ca", "p1ka", "p.i.c.a", "p!ca", "p*ca",
        "p0rra", "porr@", "p*rra", "p.o.r.r.a", "p o r r a", "put@", "pu.ta", "pu ta", "p.u.t.a", "f0da-se", "foda-se", "fo-da",
        "b0st@", "m3rda", "mer-da", "c@r@lho", "f.u.c.k", "fuuuck", "f0d4", "cu", "vai tomar no cu", "fdp", "f d p", "vsf", "pqp",
        "porn\U000000F4", "p0rn0", "nudes", "quero ver nudes", "seu corno", "seu veado", "viado", "viadooo", "v1ado", "otario",
        "seu mongol", "mongol", "seu crioulo", "crioulo", "sexo", "cacete", "bicha", "arrombado", "seu arrombado", "idiota",
        "carro prata abc1d23 pi.ca", "Gol branco ABC1D23 PUTA QUE PARIU", "carro com tesao", "vai se foder", "filho da puta",
        "FILHO DA PUTA", "fi1ho da put4", "cara de bosta", "sua merda", "buceta", "sua b0ceta", "punheta", "siririca", "xereca",
        "meu piru", "chupa pau", "chupa minha pica", "minhas tetas", "sua bunda", "seu pau", "essa rola", "minha rola",
        "cock", "dick", "shit", "bitch", "fuck", "pussy", "nigger", "whore",
    ];

    public static TheoryData<string> InnocentData() => new(Innocent);

    public static TheoryData<string> ProfaneData() => new(Profane);

    [Theory]
    [MemberData(nameof(InnocentData))]
    public void Ordinary_text_is_not_blocked(string text)
    {
        Assert.False(ProfanityFilter.IsImproper(text), "Blocked: " + text);
    }

    [Theory]
    [MemberData(nameof(ProfaneData))]
    public void Explicit_blocks_and_disguises_stay_blocked(string text)
    {
        Assert.True(ProfanityFilter.IsImproper(text), "Passed: " + text);
    }

    [Fact]
    public void Every_innocent_word_of_the_corpus_passes_alone_and_in_alert_sentences()
    {
        // Names, surnames, places, church words, car models and colours, as the alerts hold them. None of them may be blocked,
        // alone or inside a car alert; the list is meant to grow (add what the owner reports).
        var corpus = Words(Corpus());
        var blocked = new List<string>();
        var models = Words(Models());
        var colors = Words(Colors());
        var reasons = Reasons().Split('|', StringSplitOptions.TrimEntries);
        for (var i = 0; i < corpus.Length; i++)
        {
            var word = corpus[i].Replace('_', ' ');
            var sentence = $"{models[i % models.Length]} {colors[i % colors.Length]} ABC1D23 {reasons[i % reasons.Length]} {word}";
            if (ProfanityFilter.IsImproper(word) || ProfanityFilter.IsImproper(sentence))
            {
                blocked.Add(word);
            }
        }

        Assert.True(blocked.Count == 0, "Innocent words blocked: " + string.Join(", ", blocked));
    }

    [Fact]
    public void Car_alerts_built_from_models_colors_and_reasons_are_never_blocked()
    {
        var models = Words(Models());
        var colors = Words(Colors());
        var reasons = Reasons().Split('|', StringSplitOptions.TrimEntries);
        var blocked = new List<string>();
        foreach (var model in models)
        {
            foreach (var color in colors)
            {
                var reason = reasons[(model.Length * 31 + color.Length * 7) % reasons.Length];
                var text = $"{model} {color} XYZ-9K87 {reason}";
                if (ProfanityFilter.IsImproper(text))
                {
                    blocked.Add(text);
                }
            }
        }

        Assert.True(blocked.Count == 0, "Alerts blocked: " + string.Join(" / ", blocked.Take(10)));
    }

    // The dictionary (the roots of the pt-BR .dic) is a corpus of ordinary words: the number of pieces the filter blocks must
    // not grow. Today it blocks only the vulgar words and a few reviewed collisions (see ProfanityDictionaryAuditTests, which
    // names each of them); a new entry in the word list that starts to block ordinary words raises this count and fails here.
    [Fact]
    public void The_dictionary_does_not_get_more_blocked_words_than_the_reviewed_ceiling()
    {
        const int Ceiling = 95;
        var path = Path.Combine(AppContext.BaseDirectory, "Dictionaries", "pt_BR.dic");
        var clock = Stopwatch.StartNew();
        var blocked = new HashSet<string>(StringComparer.Ordinal);
        using var reader = new StreamReader(path, Encoding.UTF8);
        reader.ReadLine(); // the word count
        while (reader.ReadLine() is { } line)
        {
            var slash = line.IndexOf('/', StringComparison.Ordinal);
            var entry = slash < 0 ? line.Trim() : line[..slash].Trim();
            foreach (var piece in Pieces().Split(entry))
            {
                if (piece.Length > 0 && ProfanityFilter.IsImproper(piece))
                {
                    blocked.Add(piece);
                }
            }
        }

        clock.Stop();
        Assert.True(blocked.Count <= Ceiling, $"{blocked.Count} dictionary pieces blocked (ceiling {Ceiling}): " + string.Join(", ", blocked.Order().Take(60)));
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5), $"The dictionary scan took {clock.Elapsed.TotalSeconds:0.0} s");
    }

    private static string[] Words(string text) => WhiteSpace().Split(text).Where(word => word.Length > 0).ToArray();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhiteSpace();

    [GeneratedRegex(@"[^\p{L}\p{N}]+")]
    private static partial Regex Pieces();

    // Words joined by spaces; an underscore stands for a space inside a name.
    private static string Corpus() =>
        "Rafael Rafaela Pinto Peixoto Cassio Analista Analistas Cuca Cunha Curitiba Cuiaba Cuidado Computador Deputado Controlada Paulo Pauliceia "
        + "Pau-brasil Pica-pau Tapioca Escaravelho Bostela Porrao Porao Corporacao Cupim Cururu Cubatao Rolandia Pintor Pintura Pintinho Pintado "
        + "Piraquara Pirassununga Piracicaba Piraju Pirenopolis Putumayo Putnam Computacao Imputado Reputacao Disputa Disputado Imputar Amputado "
        + "Caracol Carapicuiba Caraguatatuba Cacetinho Escarro Mijo Cuco Cucuia Cuscuz Cuspe Cura Curso Cuba Cubo Bucelas Fortaleza Fonda Fondo "
        + "Sexta Sexta-feira Sexto Sextas Sexagenario Unissex Veadeiros Corneta Cornetas Cornelio Bichano Bichinho Bicho Mongolia Nudez Nudista "
        + "Nuvem Tesouro Tesoureiro Tesoura Porrete Porta Portao Portoes Porto Portugal Portugues Porque Porcentagem Porcao Porcoes Porco Porcos "
        + "Marica Mariana Maricota Conar Conab Conta Contas Cone Cones Conexao Conexoes Conjunto Picada Picanto Picasso Pickup Picape Pikachu Piaui "
        + "Putz Putrefato Putti Putin Pirulito Pirulitos Xote Xotes Xoxo Xerez Xerox Xerife Xerxes Bocejo Bocejar Merengue Mercado Mercedes "
        + "Mercurio Caraibas Caragua Kardec Karla Karina Kaique Kaua Kauan Keila Kelly Kevin CRLV CRV Fundacao Bitcoin Shiitake Shirley Shittim "
        + "Bastardo Bastos Titi Tito Tita Titia Boob Dickens Dickson Hancock Peacock Cocker Cockpit Cocktail Cockburn Cocktails Cocar Cocada Coco "
        + "Cocos Coxinha Coxa Coxas Cocho Cocheira Nigeria Niger Tara Tarcisio Taranto Estupidez Estupendo Estupenda Estupefato Idioma Idiomas "
        + "Idiossincrasia Vagabundear Vadiar Vadiagem Safra Safras Safari Crente Crentes Crenca Escrita Escritorio Escritura Escrituras Otavio "
        + "Otaviano Otacilio Arrombar Baba Baca Bacana Bacabal Bacia Boi Boiadeiro Boicote Boicotar Panela Panelas Panelada Viana Via Vias Viaduto "
        + "Bombeiro Bombeiros Bombons Bombom Bombas Bomba Fogo Incendio Fumaca Cantina Atabaque Cuica Berimbau Zabumba Sanfona Cavaquinho Pandeiro "
        + "Tamborim Surdo Agogo Silva Santos Oliveira Souza Rodrigues Ferreira Alves Pereira Lima Gomes Costa Ribeiro Martins Carvalho Almeida Lopes "
        + "Soares Fernandes Vieira Barbosa Rocha Dias Nascimento Andrade Moreira Nunes Marques Machado Mendes Freitas Cardoso Ramos Goncalves "
        + "Santana Teixeira Moura Cavalcante Araujo Monteiro Pinheiro Duarte Campos Correia Azevedo Batista Borges Melo Medeiros Miranda Fonseca "
        + "Castro Barros Guimaraes Coelho Leite Moraes Siqueira Xavier Tavares Bezerra Cordeiro Cruz Fogaca Pimentel Bonfim Bocaiuva Bucci Pires "
        + "Peres Pera Perez Pacheco Paiva Paixao Pontes Prado Pretti Pucci Pupo Puppim Picolo Piccolo Picoli Pichetti Picinini Piovesan Pizzolato "
        + "Piroli Analice Analia Analu Anai Ananias Anacleto Cassia Cassiano Cassius Cacilda Caca Caco Cacau Carla Carlos Carolina Caio Camila "
        + "Camilo Cecilia Cesar Cintia Claudia Claudio Clara Cleber Clovis Conceicao Constanca Cristiane Cristina Cristiano Cristovao Custodio "
        + "Cirilo Dalva Dalila Dara Dario Dayane Diego Dirce Douglas Edgar Edna Eduardo Elaine Eliana Elisa Emanuel Emerson Erica Erick Estela "
        + "Eunice Evandro Fabiana Fabio Fatima Felipe Fernanda Fernando Flavia Flavio Francisco Gabriela Geraldo Giovana Giovanni Gisele Gustavo "
        + "Helena Henrique Hugo Igor Ingrid Isabela Isadora Ivone Jaqueline Jessica Joana Joaquim Jonas Juliana Julio Karen Katia Lais Larissa "
        + "Laura Leandro Leila Leonardo Leticia Livia Luana Lucia Luciana Luciano Luis Luiza Marcela Marcelo Marcia Marcio Marcos Margarida Mario "
        + "Matheus Mauro Monica Natalia Nelson Nicolas Olivia Patricia Raimundo Renata Renato Ricardo Roberta Roberto Rodrigo Rogerio Ronaldo Rosa "
        + "Sabrina Samara Sandra Sergio Silvia Simone Sonia Sueli Tatiana Teresa Thiago Tomas Valeria Vanessa Vera Vicente Victor Vinicius Viviane "
        + "Wagner Wellington Wesley Yara Yuri Zelia Pirasol Piramide Pirataria Pirao Piranha Piranhas Pirai Pirapora Pirapozinho Piracanjuba "
        + "Piracaia Piraja Pirapetinga Pituba Pitanga Pitangueiras Pitangui Pitombeira Bocaina Bocaiuva Boca Bocage Bocadinho Cacimbinha Cacique "
        + "Cacimba Cacoal Cacos Cacto Cactus Caraca Carabina Caracas Caracu Caracois Carajas Caramba Carambola Caramelo Caramuru Caranguejo "
        + "Carapuca Caravana Caravelas Carbono Carburador Cardume Cardeal Cardapio Careca Caretas Carencia Cargo Cargos Caridade Carinho Mercearia "
        + "Merces Conforto Confianca Confissao Xaxim Xavante Xingu Xique Xinguara Xanxere Coxim Coxilha Cunhatai Cupira Curaca Curimata Curvelo "
        + "Cururupu Custodia Pinhal Pinhao Pinheiros Pinheiral Pinhalzinho Pindamonhangaba Pindoba Pindorama Pindaiba Pinda Pinoquio Pinguim "
        + "Pingos Pingo Pintada Pintos Pintas Peitoral Peitoril Tetanico Tetraplegico Tetra Tetris Teteia Rolamento Rolha Rolhas Rolo Rolos Roleta "
        + "Rolim Rolante Rolinha Mala Malas Malabarista Malandro Malaria Malha Malhas Malhacao Burro Burros Burrice Gata Gato Gatos Gatas Vaca "
        + "Vacas Vacinacao Vacina Vacinas Galinha Galinhas Galinheiro Galo Galos Macaco Macacos Macacao Cachorro Cachorros Cachorrinho Cadela "
        + "Farol Farois Farolete Banco Bancos Bancario Banca Bancada Fusca Fuscas Peru Perus Perua Peruca Pomba Pombas Pombo Pombos Lagarta "
        + "Lagarto Lagartixa Vadio Vadios Pomar Pomares Culto Cultos Culturas Cultural Cultura Culpa Culpado Cumprimento Cumprir Cunhado Cunhada "
        + "Cuidar Cuidados Cuidadoso Curar Curativo Curitibano Cursinho Custo Custos Cutucar Escuta Escutar Escudo Escultura Escusa Acusar Acusado "
        + "Recusar Recusado Ocupado Ocupar Ocupacao Educacao Educado Executar Educar Eucaristia Eucalipto Euro Europa Europeu "
        + "Rafael_Souza Rafael_Cunha Rafael_Peixoto Pastor_Rafael Pastor_Cassio Irmao_Pinto Irma_Pinto Diacono_Pinto Seu_Paulo Dona_Paula "
        + "Tio_Paulo Tia_Cuca Joao_Pinto Pinto_Silva Pinto_Coelho Pinto_Bandeira Sao_Paulo Porto_Alegre Porto_Velho Santa_Ceia Vidro_aberto "
        + "Farol_aceso Pneu_furado Carro_na_calcada Bloqueando_saida Alarme_tocando Porta_aberta Luz_acesa Som_alto Pisca_alerta_ligado "
        + "Pau_de_selfie Pau-brasil Pica-pau Pica_pau Pau_de_sebo Colher_de_pau Pau_a_pique Pau-de-arara Pau-ferro Pau-rosa Pau-d'arco "
        + "Peito_de_frango Dor_no_peito Sala_de_amamentacao Costa_e_Silva";

    private static string Models() =>
        "Gol Uno Palio Siena Celta Corsa Onix Prisma HB20 Kwid Mobi Argo Cronos Pulse Fastback Nivus Tracker Creta Strada Toro Saveiro Hilux "
        + "Ranger S10 L200 Fusca Kombi Brasilia Fox Up Polo Jetta Golf T-Cross Kicks Renegade Compass Civic City Fit HR-V Corolla Yaris Etios "
        + "Cruze Cobalt Spin Sandero Logan Duster Captur Punto Doblo Ducato Fiorino Picanto Sportage Cerato Sorento Pajero ASX Lancer Outlander "
        + "Ka Fiesta Focus EcoSport Escort Fusion Edge Mustang Monza Vectra Astra Kadett Chevette Opala Omega Meriva Zafira Montana Equinox "
        + "Camaro Blazer Silverado Parati Santana Voyage Passat Tiguan Amarok Taos Virtus Camry Bandeirante Biz Titan Fan Bros Pop PCX XRE "
        + "Twister Hornet Lead Elite Fazer Factor Crosser NMax Neo Lander Tenere Burgman Ninja Citycom Chopper Wrangler Defender Jimny Cooper X1 "
        + "A3 C180 XC60 208 C3 C4 Cactus Pallas Picasso Mini Fortwo Sprinter Master Boxer Jumper Daily Besta Topic Hiace Van Gurgel Tempra Tipo "
        + "Elba Premio Oggi Spazio Marea Stilo Bravo Brava Idea Linea Freemont Mille Fire Way";

    private static string Colors() =>
        "branco branca preto preta prata cinza grafite vermelho vermelha vinho azul verde amarelo amarela laranja roxo rosa marrom bege "
        + "champanhe dourado dourada bronze cobre perolado fosco metalico turquesa violeta lilas creme caramelo cafe chumbo goiaba terracota "
        + "salmao carmim cereja mostarda oliva petroleo areia gelo azul-marinho cinza-escuro cinza-chumbo verde-musgo";

    private static string Reasons() =>
        "bomba|vidro aberto|vidro quebrado|farol aceso|pneu furado|pneu vazio|carro na calcada|bloqueando saida|bloqueando a saida|"
        + "bloqueando garagem|bloqueando o portao|estacionado em local proibido|alarme tocando|alarme disparado|carro ligado|porta aberta|"
        + "porta-malas aberto|mala aberta|capo aberto|luz acesa|luz de freio acesa|luz interna acesa|seta ligada|pisca alerta ligado|som alto|"
        + "som ligado|radio ligado|celular dentro do carro|bolsa no banco|bolsa a vista|crianca no carro|cachorro no carro|gato no carro|"
        + "bomba de gasolina|vazamento de oleo|vazamento de agua|vazando combustivel|cheiro de gas|cheiro de queimado|fumaca saindo do capo|"
        + "carro pegando fogo|pau de selfie no carro|pau-brasil no porta-malas|pica-pau no capo|farol baixo aceso|farol alto aceso|"
        + "luz de re acesa|bateria arriada|tanque aberto|retrovisor quebrado|para-brisa quebrado|para-choque solto|placa caida|"
        + "carro arrombado|vidro arrombado|porta arrombada|janela aberta|teto solar aberto|carro na rampa|carro na faixa|"
        + "carro na vaga de idoso|carro em fila dupla|carro atravessado|carro na frente da garagem";
}
