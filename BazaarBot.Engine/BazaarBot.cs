using SimpleJSON;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace BazaarBot.Engine
{
    public class BazaarBot
    {
        public int TotalRounds { get; private set; }

        public List<string> CommodityClasses { get; set; }
        
        public Dictionary<string, AgentClass> AgentClasses { get; set; }
        public List<Agent> Agents { get; set; }

        public Dictionary<string, List<Offer>> Bids = new Dictionary<string, List<Offer>>();
        public Dictionary<string, List<Offer>> Asks = new Dictionary<string, List<Offer>>();
        public Dictionary<string, List<float>> ProfitHistory = new Dictionary<string, List<float>>();
        public Dictionary<string, List<float>> PriceHistory = new Dictionary<string, List<float>>();	//avg clearing price per good over time
        public Dictionary<string, List<float>> AskHistory = new Dictionary<string,List<float>>();		//# ask (sell) offers per good over time
        public Dictionary<string, List<float>> BidHistory = new Dictionary<string,List<float>>();		//# bid (buy) offers per good over time
        public Dictionary<string, List<float>> VarHistory = new Dictionary<string, List<float>>();		//# bid (buy) offers per good over time
        public Dictionary<string, List<float>> TradeHistory = new Dictionary<string,List<float>>();   //# units traded per good over time
        public static Dictionary<string, float> Production = new Dictionary<string,float>();

        public static IRandomNumberGenerator RNG;

        public BazaarBot(IRandomNumberGenerator rng)
        {
            RNG = rng;
        }

        public void simulate(int rounds)
        {
            Production = new Dictionary<string, float>();
		    for (int round = 0 ; round < rounds; round++) {
                TotalRounds++;
			    foreach (var agent in Agents) {
				    agent.set_money_last(agent.Money);
				
				    var ac = AgentClasses[agent.ClassId];				
				    ac.logic.Perform(agent);
								
				    foreach (var commodity in CommodityClasses) {
					    agent.generate_offers(this, commodity);
				    }
			    }
			    foreach (var commodity in CommodityClasses){
				    resolve_offers(commodity);
			    }
			    foreach (var agent in Agents.ToList()) {
				    if (agent.Money <= 0) {
					    replaceAgent(agent);
				    }
			    }
		    }

	    }

        public void ask(Offer offer)
        {
            Asks[offer.Commodity].Add(offer);
        }

        public void bid(Offer offer)
        {
            Bids[offer.Commodity].Add(offer);
        }

        public float GetPriceAverage(string commodity, int range)
        {
            return Average(PriceHistory[commodity], range);
        }

        public float GetProfitAverage(string commodity, int range)
        {
            var list = ProfitHistory[commodity];
            return Average(list, range);
        }

        public float GetAskAverage(string commodity, int range)
        {
            return Average(AskHistory[commodity], range);
        }

        public float GetBidAverage(string commodity, int range)
        {
            return Average(BidHistory[commodity], range);
        }

        public float GetTradeAverage(string commodity, int range)
        {
            var list = TradeHistory[commodity];
            return Average(list, range);
        }

        public List<string> get_commodities_unsafe()
        {
            return CommodityClasses;
        }

        private void resolve_offers(string commodity = "")
        {
		    var bids = Bids[commodity];
		    var asks = Asks[commodity];		
		
		    //shuffle the books
		    shuffle(bids);
		    shuffle(asks);
		
		    bids.Sort(sort_decreasing_price);	//highest buying price first
		    asks.Sort(sort_increasing_price);	//lowest selling price first
		
		    int successful_trades = 0;		//# of successful trades this round
		    float money_traded = 0;			//amount of money traded this round
		    float units_traded = 0;			//amount of goods traded this round
		    float avg_price;				//avg clearing price this round
		    float num_asks= 0;
		    float num_bids= 0;
		
		    int failsafe = 0;
		
		    for (int i = 0; i< bids.Count;i++)
            {
			    num_bids += bids[i].Units;
		    }
		
		    for (int i = 0; i<asks.Count;i++)
            {
			    num_asks += asks[i].Units;
		    }
				
		    //march through and try to clear orders
		    while (bids.Count > 0 && asks.Count > 0) {	//while both books are non-empty
			    var buyer = bids[0];
			    var seller = asks[0];
			
			    var quantityTraded = Math.Min(seller.Units, buyer.Units);
			    var clearingPrice = avgf(seller.UnitPrice, buyer.UnitPrice);
						
			    if (quantityTraded > 0) {
				    //transfer the goods for the agreed price
                    seller.Trade(quantityTraded);
                    buyer.Trade(quantityTraded);
							
				    transfer_commodity(commodity, quantityTraded, seller.AgentId, buyer.AgentId);
				    TransferMoney(quantityTraded * clearingPrice, seller.AgentId, buyer.AgentId);
									
				    //update agent price beliefs based on successful transaction
				    var buyer_a  = Agents[buyer.AgentId];
				    var seller_a = Agents[seller.AgentId];
				    buyer_a.update_price_model(this, "buy", commodity, true, clearingPrice);
				    seller_a.update_price_model(this, "sell", commodity, true, clearingPrice);
				
				    //log the stats
				    money_traded += (quantityTraded * clearingPrice);
				    units_traded += quantityTraded;
				    successful_trades++;							
			    }
						
			    if (seller.Units == 0) {	//seller is out of offered good
				    asks.Remove(asks[0]);		//remove ask
				    failsafe = 0;
			    }
			    if (buyer.Units == 0) {		//buyer is out of offered good
				    bids.Remove(bids[0]);		//remove bid
				    failsafe = 0;
			    }
			
			    failsafe++;
			
			    if (failsafe > 1000) {
				    Console.WriteLine("BOINK!");		
			    }
		    }
		
		    //reject all remaining offers, 
		    //update price belief models based on unsuccessful transaction
		    while(bids.Count > 0){
			    var buyer = bids[0];
			    var buyer_a = Agents[buyer.AgentId];
			    buyer_a.update_price_model(this,"buy",commodity, false);
                bids.Remove(bids[0]);
		    }
		    while(asks.Count > 0){
			    var seller = asks[0];
			    var seller_a = Agents[seller.AgentId];
			    seller_a.update_price_model(this,"sell",commodity, false);
                asks.Remove(asks[0]);
		    }
		
		    //update history		
            AskHistory[commodity].Add(num_asks);
		    BidHistory[commodity].Add(num_bids);
            VarHistory[commodity].Add(num_asks - num_bids);
            TradeHistory[commodity].Add(units_traded);
		
		    if(units_traded > 0){
			    avg_price = money_traded / (float)units_traded;
                PriceHistory[commodity].Add(avg_price);
		    }else {
			    //special case: none were traded this round, use last round's average price
                PriceHistory[commodity].Add(GetPriceAverage(commodity, 1));
			    avg_price = GetPriceAverage(commodity,1);
		    }		
		
		    Agents.Sort(sort_agent_alpha);
		    var curr_class = "";
		    var last_class = "";
            List<float> list = null;                                                                                    
		
            for (int i=0;i < Agents.Count;i++) {
			    var a = Agents[i];		//get current agent
			    curr_class = a.ClassId;			//check its class
			    if (curr_class != last_class) {		//new class?
                    if (list != null) //do we have a list built up?
                    {				
					    //log last class' profit
                        ProfitHistory[last_class].Add(Average(list));
				    }
				    list = new List<float>();		//make a new list
				    last_class = curr_class;		
			    }
			    list.Add(a.get_profit());			//push profit onto list
		    }	
		    //add the last class too
            ProfitHistory[last_class].Add(Average(list));
		
		    //sort by id so everything works again
		    Agents.Sort(sort_agent_id);
		
	    }

        private void replaceAgent(Agent agent)
        {
            // force at least one of each class
            var best_id = GetMissingClass() ?? MostProfitableAgentClass();

            var agent_class = AgentClasses[best_id];
            var new_agent = new Agent(agent.Id, best_id, agent_class.GetStartInventory(), agent_class.money);
            new_agent.init(this);
            Agents[agent.Id] = new_agent;
            agent.Destroyed = true;
        }

        private string GetUnderservedMarket()
        {
            var best_opportunity = get_best_market_opportunity();
            if (best_opportunity != "")
            {
                var best_opportunity_class = get_agent_class_that_makes_most(best_opportunity);
                if (best_opportunity_class != "")
                {
                    return best_opportunity_class;
                }
            }
            return null;
        }

        private string GetMissingClass()
        {
            foreach (var classId in AgentClasses.Keys)
            {
                if (!Agents.Any(p => p.ClassId == classId))
                    return classId;
            }
            return null;
        }

        private string get_agent_class_that_makes_most(string commodity_)
        {
            float best_amount = 0;
            var best_class = "";
            foreach (var key in AgentClasses.Keys)
            {
                var ac = AgentClasses[key];
                var amount = ac.logic.GetProduction(commodity_);
                if (amount > best_amount)
                {
                    best_amount = amount;
                    best_class = ac.id;
                }
            }
            return best_class;
        }

        private string get_agent_class_with_most(string commodity_)
        {
            var amount = 0f;
            var best_amount = 0f;
            var best_class = "";
            foreach (var key in AgentClasses.Keys)
            {
                amount = get_avg_inventory(key, commodity_);
                if (amount > best_amount)
                {
                    best_amount = amount;
                    best_class = key;
                }
            }
            return best_class;
        }

        private float get_avg_inventory(string agent_id, string commodity_)
        {
            var list = Agents.Where(p => p.ClassId == agent_id);
            var amount = 0f;
            foreach (var agent in list)
            {
                amount += agent.QueryInventory(commodity_);
            }
            amount /= list.Count();
            return amount;
        }

        /**
         * Get the market with the highest demand/supply ratio over time
         * @param   minimum the minimum demand/supply ratio to consider an opportunity
         * @param	range number of rounds to look back
         * @return
         */

        private string get_best_market_opportunity(float minimum = 1.5f, int range = 10)
        {
            var best_market = "";
            var best_ratio = -999999f;
            foreach (var commodity in CommodityClasses)
            {
                var asks = GetAskAverage(commodity, range);
                var bids = GetBidAverage(commodity, range);
                var ratio = 0f;
                if (asks == 0 && bids > 0)
                {
                    ratio = 9999999999999999;
                }
                else
                {
                    ratio = bids / asks;
                }
                if (ratio > minimum && ratio > best_ratio)
                {
                    best_ratio = ratio;
                    best_market = commodity;
                }
            }
            return best_market;
        }

        private string MostProfitableAgentClass(int range = 10)
        {
            return AgentClasses.OrderByDescending(p => GetProfitAverage(p.Key, range)).Select(p => p.Key).First();
        }

        private void transfer_commodity(string commodity_, float units_, int seller_id, int buyer_id)
        {
            var seller = Agents[seller_id];
            var buyer = Agents[buyer_id];
            seller.ChangeInventory(commodity_, -units_);
            buyer.ChangeInventory(commodity_, units_);
        }

        private void TransferMoney(float amount, int sellerId, int buyerId)
        {
            var seller = Agents[sellerId];
            var buyer = Agents[buyerId];
            seller.Money += amount;
            buyer.Money -= amount;
        }

        private static int sort_agent_id(Agent a, Agent b)
        {
            if (a.Id < b.Id) return -1;
            if (a.Id > b.Id) return 1;
            return 0;
        }

        private static int sort_agent_alpha(Agent a, Agent b)
        {
            return string.Compare(a.ClassId, b.ClassId);
        }

        private static int sort_decreasing_price(Offer a, Offer b)
        {
            //Decreasing means: highest first
            if (a.UnitPrice < b.UnitPrice) return 1;
            if (a.UnitPrice > b.UnitPrice) return -1;
            return 0;
        }

        private static int sort_increasing_price(Offer a, Offer b)
        {
            //Increasing means: lowest first
            if (a.UnitPrice > b.UnitPrice) return 1;
            if (a.UnitPrice < b.UnitPrice) return -1;
            return 0;
        }

        private static float avgf(float a, float b)
        {
            return (a + b) / 2;
        }

        private static List<T> shuffle<T>(List<T> list)
        {
            for (int i = 0; i < list.Count - 1; i++)
            {
                var ii = (list.Count - 1) - i;
                if (ii > 1)
                {
                    var j = RNG.Int(0, ii);
                    var temp = list[j];
                    list[j] = list[ii];
                    list[ii] = temp;
                }
            }
            return list;
        }

        public static float Average(IEnumerable<float> values)
        {
            return Average(values, values.Count());
        }

        private static float Average(IEnumerable<float> values, int range)
        {
            return values.Any() ? values.Reverse().Take(range).Average() : 0;
        }

        private static float Average(IEnumerable<int> values, int range)
        {
            return Average(values.Select(p => (float)p), range);
        }

        internal static void RecordProduction(string target, float amount)
        {
            if (!Production.ContainsKey(target))
                Production[target] = 0f;
            Production[target] += amount;
        }
    }
}