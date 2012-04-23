// Copyright (C) 2012 Zeno Gantner
//
// This file is part of MyMediaLite.
//
// MyMediaLite is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// MyMediaLite is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU General Public License for more details.
//
//  You should have received a copy of the GNU General Public License
//  along with MyMediaLite.  If not, see <http://www.gnu.org/licenses/>.
//

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using MyMediaLite.Data;
using MyMediaLite.DataType;
using MyMediaLite.IO;
using MyMediaLite.Util;

namespace MyMediaLite.RatingPrediction
{
	/// <summary>
	///   Asymmetric factor model which represents items in terms of the users that rated them,
	///   and users in terms of the items they rated
	/// </summary>
	/// <remarks>
	///   <para>
	///     Literature:
	///     <list type="bullet">
	///       <item><description>
	///         Arkadiusz Paterek:
	///         Improving regularized singular value decomposition for collaborative filtering.
	///         KDD Cup 2007.
	///         http://arek-paterek.com/ap_kdd.pdf
	///       </description></item>
	///     </list>
	///   </para>
	/// </remarks>
	public class SigmoidCombinedAsymmetricFactorModel : BiasedMatrixFactorization, ITransductiveRatingPredictor
	{
		int[][] users_who_rated_the_item;
		int[][] items_rated_by_user;

		/// <summary>item factors (part expressed via the users who rated them)</summary>
		Matrix<float> x;
		/// <summary>user factors (part expressed via the rated items)</summary>
		Matrix<float> y;

		///
		public IDataSet AdditionalFeedback { get; set; }

		/// <summary>Default constructor</summary>
		public SigmoidCombinedAsymmetricFactorModel() : base()
		{
			AdditionalFeedback = new PosOnlyFeedback<SparseBooleanMatrix>(); // in case no test data is provided
			Regularization = 0.015f;
			LearnRate = 0.001f;
			BiasLearnRate = 0.7f;
			BiasReg = 0.33f;
		}

		///
		public override void Train()
		{
			MaxUserID = Math.Max(ratings.MaxUserID, AdditionalFeedback.MaxUserID);
			MaxItemID = Math.Max(ratings.MaxItemID, AdditionalFeedback.MaxItemID);
			users_who_rated_the_item = this.UsersWhoRated();
			items_rated_by_user = this.ItemsRatedByUser();
			base.Train();
		}

		///
		public override float Predict(int user_id, int item_id)
		{
			if (user_factors == null)
				PrecomputeUserFactors();
			if (item_factors == null)
				PrecomputeItemFactors();
			return base.Predict(user_id, item_id);
		}

		///
		protected override void Iterate(IList<int> rating_indices, bool update_user, bool update_item)
		{
			SetupLoss();

			float reg_u = RegU;  // to limit property accesses
			float reg_i = RegI;
			float lr = LearnRate;

			foreach (int index in rating_indices)
			{
				int u = ratings.Users[index];
				int i = ratings.Items[index];

				double score = global_bias + user_bias[u] + item_bias[i];

				var u_plus_y_sum_vector = y.SumOfRows(items_rated_by_user[u]);
				double u_norm_denominator = Math.Sqrt(items_rated_by_user[u].Length);
				for (int f = 0; f < u_plus_y_sum_vector.Count; f++)
					u_plus_y_sum_vector[f] = (float) (u_plus_y_sum_vector[f] / u_norm_denominator);

				var i_plus_x_sum_vector = x.SumOfRows(users_who_rated_the_item[i]);
				double i_norm_denominator = Math.Sqrt(users_who_rated_the_item[i].Length);
				for (int f = 0; f < i_plus_x_sum_vector.Count; f++)
					i_plus_x_sum_vector[f] = (float) (i_plus_x_sum_vector[f] / i_norm_denominator);

				score += DataType.VectorExtensions.ScalarProduct(u_plus_y_sum_vector, i_plus_x_sum_vector);
				double sig_score = 1 / (1 + Math.Exp(-score));

				double prediction = min_rating + sig_score * rating_range_size;
				double err = ratings[index] - prediction;

				float user_reg_weight = FrequencyRegularization ? (float) (reg_u / Math.Sqrt(ratings.CountByUser[u])) : reg_u;
				float item_reg_weight = FrequencyRegularization ? (float) (reg_i / Math.Sqrt(ratings.CountByItem[i])) : reg_i;
				float gradient_common = compute_gradient_common(sig_score, err);

				// adjust biases
				if (update_user)
					user_bias[u] += BiasLearnRate * lr * (gradient_common - BiasReg * user_reg_weight * user_bias[u]);
				if (update_item)
					item_bias[i] += BiasLearnRate * lr * (gradient_common - BiasReg * item_reg_weight * item_bias[i]);

				// adjust factors
				double tmp_u = gradient_common / u_norm_denominator; // TODO better name than tmp_u
				double tmp_i = gradient_common / i_norm_denominator; // TODO better name than tmp_i
				for (int f = 0; f < NumFactors; f++)
				{
					float u_f = u_plus_y_sum_vector[f];
					float i_f = i_plus_x_sum_vector[f];

					// if necessary, compute and apply updates
					if (update_user)
					{
						double delta_u = gradient_common * i_plus_x_sum_vector[f] - user_reg_weight * u_f;
						user_factors.Inc(u, f, lr * delta_u);

						double common_update = tmp_i * u_f;
						foreach (int other_user_id in users_who_rated_the_item[i])
						{
							int other_user_rating_count = other_user_id > ratings.MaxUserID            ? 0 : ratings.CountByUser[other_user_id];
							other_user_rating_count    += other_user_id > AdditionalFeedback.MaxUserID ? 0 : AdditionalFeedback.CountByUser[other_user_id];

							float rated_user_reg = FrequencyRegularization ? (float) (reg_u / Math.Sqrt(other_user_rating_count)) : reg_u;
							double delta_ou = common_update - rated_user_reg * x[other_user_id, f];
							x.Inc(other_user_id, f, lr * delta_ou);
						}
					}

					// if necessary, compute and apply updates
					if (update_item)
					{
						double delta_i = gradient_common * u_plus_y_sum_vector[f] - item_reg_weight * i_f;
						item_factors.Inc(i, f, lr * delta_i);

						double common_update = tmp_u * i_f;
						foreach (int other_item_id in items_rated_by_user[u])
						{
							int other_item_rating_count = other_item_id > ratings.MaxItemID            ? 0 : ratings.CountByItem[other_item_id];
							other_item_rating_count    += other_item_id > AdditionalFeedback.MaxItemID ? 0 : AdditionalFeedback.CountByItem[other_item_id];

							float rated_item_reg = FrequencyRegularization ? (float) (reg_i / Math.Sqrt(other_item_rating_count)) : reg_i;
							double delta_oi = common_update - rated_item_reg * y[other_item_id, f];
							y.Inc(other_item_id, f, lr * delta_oi);
						}
					}
				}
			}
			user_factors = null; // delete old user factors
			item_factors = null; // delete old item factors
		}

		///
		public override void SaveModel(string filename)
		{
			using ( StreamWriter writer = Model.GetWriter(filename, this.GetType(), "3.00") )
			{
				writer.WriteLine(global_bias.ToString(CultureInfo.InvariantCulture));
				writer.WriteLine(min_rating.ToString(CultureInfo.InvariantCulture));
				writer.WriteLine(max_rating.ToString(CultureInfo.InvariantCulture));
				writer.WriteVector(user_bias);
				writer.WriteVector(item_bias);
				writer.WriteMatrix(x);
				writer.WriteMatrix(user_factors);
			}
		}

		///
		public override void LoadModel(string filename)
		{
			using ( StreamReader reader = Model.GetReader(filename, this.GetType()) )
			{
				var global_bias = float.Parse(reader.ReadLine(), CultureInfo.InvariantCulture);
				var min_rating  = float.Parse(reader.ReadLine(), CultureInfo.InvariantCulture);
				var max_rating  = float.Parse(reader.ReadLine(), CultureInfo.InvariantCulture);
				var user_bias = reader.ReadVector();
				var item_bias = reader.ReadVector();
				var y            = (Matrix<float>) reader.ReadMatrix(new Matrix<float>(0, 0));
				var user_factors = (Matrix<float>) reader.ReadMatrix(new Matrix<float>(0, 0));

				if (user_bias.Count != user_factors.dim1)
					throw new IOException(
						string.Format(
							"Number of users must be the same for biases and factors: {0} != {1}",
							user_bias.Count, user_factors.dim1));

				if (y.NumberOfColumns != user_factors.NumberOfColumns)
					throw new Exception(
						string.Format("Number of item (y) and user factors must match: {0} != {1}",
							y.NumberOfColumns, user_factors.NumberOfColumns));

				this.MaxUserID = user_bias.Count - 1;
				this.MaxItemID = item_bias.Count - 1;

				// assign new model
				this.global_bias = global_bias;
				if (this.NumFactors != user_factors.NumberOfColumns)
				{
					Console.Error.WriteLine("Set NumFactors to {0}", user_factors.NumberOfColumns);
					this.NumFactors = (uint) user_factors.NumberOfColumns;
				}
				this.user_bias = user_bias.ToArray();
				this.item_bias = item_bias.ToArray();
				this.x = y;
				this.user_factors = user_factors;
				this.min_rating = min_rating;
				this.max_rating = max_rating;
			}
		}

		///
		public override float ComputeObjective()
		{
			double complexity = 0;
			if (FrequencyRegularization)
			{
				for (int u = 0; u <= MaxUserID; u++)
				{
					int count_by_user = u > ratings.MaxUserID            ? 0 : ratings.CountByUser[u];
					count_by_user    += u > AdditionalFeedback.MaxUserID ? 0 : AdditionalFeedback.CountByUser[u];
					complexity += Math.Sqrt(count_by_user) * RegU           * Math.Pow(x.GetRow(u).EuclideanNorm(), 2);
					complexity += Math.Sqrt(count_by_user) * RegU * BiasReg * Math.Pow(user_bias[u], 2);
				}
				for (int i = 0; i <= MaxItemID; i++)
				{
					int count_by_item = i > ratings.MaxItemID            ? 0 : ratings.CountByItem[i];
					count_by_item    += i > AdditionalFeedback.MaxItemID ? 0 : AdditionalFeedback.CountByItem[i];
					complexity += Math.Sqrt(count_by_item) * RegI           * Math.Pow(y.GetRow(i).EuclideanNorm(), 2);
					complexity += Math.Sqrt(count_by_item) * RegI * BiasReg * Math.Pow(item_bias[i], 2);
				}
			}
			else
			{
				for (int u = 0; u <= MaxUserID; u++)
				{
					int count_by_user = u > ratings.MaxUserID            ? 0 : ratings.CountByUser[u];
					count_by_user    += u > AdditionalFeedback.MaxUserID ? 0 : AdditionalFeedback.CountByUser[u];
					complexity += count_by_user * RegU * BiasReg * Math.Pow(user_bias[u], 2);
					complexity += count_by_user * RegU * Math.Pow(x.GetRow(u).EuclideanNorm(), 2);
				}
				for (int i = 0; i <= MaxItemID; i++)
				{
					int count_by_item = i > ratings.MaxItemID            ? 0 : ratings.CountByItem[i];
					count_by_item    += i > AdditionalFeedback.MaxItemID ? 0 : AdditionalFeedback.CountByItem[i];
					complexity += count_by_item * RegI * Math.Pow(y.GetRow(i).EuclideanNorm(), 2);
					complexity += count_by_item * RegI * BiasReg * Math.Pow(item_bias[i], 2);
				}
			}

			return (float) (ComputeLoss() + complexity);
		}

		///
		protected override float[] FoldIn(IList<Pair<int, float>> rated_items)
		{
			throw new NotImplementedException();
		}

		///
		protected override void InitModel()
		{
			x = new Matrix<float>(MaxItemID + 1, NumFactors);
			x.InitNormal(InitMean, InitStdDev);
			// set factors to zero for users without training examples
			for (int user_id = 0; user_id < x.NumberOfRows; user_id++)
				if (user_id > ratings.MaxUserID || ratings.CountByUser[user_id] == 0)
					x.SetRowToOneValue(user_id, 0);

			y = new Matrix<float>(MaxItemID + 1, NumFactors);
			y.InitNormal(InitMean, InitStdDev);
			// set factors to zero for items without training examples
			for (int item_id = 0; item_id < y.NumberOfRows; item_id++)
				if (item_id > ratings.MaxItemID || ratings.CountByItem[item_id] == 0)
					y.SetRowToOneValue(item_id, 0);

			base.InitModel();
		}

		/// <summary>Precompute all user factors</summary>
		protected void PrecomputeUserFactors()
		{
			if (user_factors == null)
				user_factors = new Matrix<float>(MaxUserID + 1, NumFactors);

			if (items_rated_by_user == null)
				items_rated_by_user = this.ItemsRatedByUser();

			for (int user_id = 0; user_id <= MaxUserID; user_id++)
				PrecomputeUserFactors(user_id);
		}

		/// <summary>Precompute the factors for a given user</summary>
		/// <param name='user_id'>the ID of the user</param>
		protected void PrecomputeUserFactors(int user_id)
		{
			if (items_rated_by_user[user_id].Length == 0)
				return;

			// compute
			var factors = y.SumOfRows(items_rated_by_user[user_id]);
			double norm_denominator = Math.Sqrt(items_rated_by_user[user_id].Length);
			for (int f = 0; f < factors.Count; f++)
				factors[f] = (float) (factors[f] / norm_denominator);

			// assign
			for (int f = 0; f < factors.Count; f++)
				user_factors[user_id, f] = (float) factors[f];
		}

		/// <summary>Precompute all item factors</summary>
		protected void PrecomputeItemFactors()
		{
			if (item_factors == null)
				item_factors = new Matrix<float>(MaxItemID + 1, NumFactors);

			if (users_who_rated_the_item == null)
				users_who_rated_the_item = this.UsersWhoRated();

			for (int item_id = 0; item_id <= MaxItemID; item_id++)
				PrecomputeItemFactors(item_id);
		}

		/// <summary>Precompute the factors for a given item</summary>
		/// <param name='item_id'>the ID of the item</param>
		protected void PrecomputeItemFactors(int item_id)
		{
			if (users_who_rated_the_item[item_id].Length == 0)
				return;

			// compute
			var factors = x.SumOfRows(users_who_rated_the_item[item_id]);
			double norm_denominator = Math.Sqrt(users_who_rated_the_item[item_id].Length);
			for (int f = 0; f < factors.Count; f++)
				factors[f] = (float) (factors[f] / norm_denominator);

			// assign
			for (int f = 0; f < factors.Count; f++)
				item_factors[item_id, f] = (float) factors[f];
		}

		///
		public override string ToString()
		{
			return string.Format(
				CultureInfo.InvariantCulture,
				"{0} num_factors={1} regularization={2} bias_reg={3} frequency_regularization={4} learn_rate={5} bias_learn_rate={6} num_iter={7} loss={8}",
				this.GetType().Name, NumFactors, Regularization, BiasReg, FrequencyRegularization, LearnRate, BiasLearnRate, NumIter, Loss);
		}
	}
}

